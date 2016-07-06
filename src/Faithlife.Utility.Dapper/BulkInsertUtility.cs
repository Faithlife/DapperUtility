using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Faithlife.Utility.Dapper
{
	/// <summary>
	/// Methods for bulk insert with Dapper.
	/// </summary>
	/// <remarks>https://github.com/Faithlife/DapperUtility/blob/master/docs/BulkInsert.md</remarks>
	public static class BulkInsertUtility
	{
		/// <summary>
		/// Efficiently inserts multiple rows, in batches as necessary.
		/// </summary>
		public static int BulkInsert<TInsert>(this IDbConnection connection, string sql, IEnumerable<TInsert> insertParams, IDbTransaction transaction = null, int? batchSize = null)
		{
			return connection.BulkInsert(sql, (object) null, insertParams, transaction, batchSize);
		}

		/// <summary>
		/// Efficiently inserts multiple rows, in batches as necessary.
		/// </summary>
		public static int BulkInsert<TCommon, TInsert>(this IDbConnection connection, string sql, TCommon commonParam, IEnumerable<TInsert> insertParams, IDbTransaction transaction = null, int? batchSize = null)
		{
			int rowCount = 0;
			foreach (var commandDefinition in GetBulkInsertCommands(sql, commonParam, insertParams, transaction, batchSize))
				rowCount += connection.Execute(commandDefinition);
			return rowCount;
		}

		/// <summary>
		/// Efficiently inserts multiple rows, in batches as necessary.
		/// </summary>
		public static Task<int> BulkInsertAsync<TInsert>(this IDbConnection connection, string sql, IEnumerable<TInsert> insertParams, IDbTransaction transaction = null, int? batchSize = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			return connection.BulkInsertAsync(sql, (object) null, insertParams, transaction, batchSize, cancellationToken);
		}

		/// <summary>
		/// Efficiently inserts multiple rows, in batches as necessary.
		/// </summary>
		public static async Task<int> BulkInsertAsync<TCommon, TInsert>(this IDbConnection connection, string sql, TCommon commonParam, IEnumerable<TInsert> insertParams, IDbTransaction transaction = null, int? batchSize = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			int rowCount = 0;
			foreach (var commandDefinition in GetBulkInsertCommands(sql, commonParam, insertParams, transaction, batchSize, cancellationToken))
				rowCount += await connection.ExecuteAsync(commandDefinition).ConfigureAwait(false);
			return rowCount;
		}

		/// <summary>
		/// Gets the Dapper <c>CommandDefinition</c>s used by <c>BulkInsert</c> and <c>BulkInsertAsync</c>.
		/// </summary>
		public static IEnumerable<CommandDefinition> GetBulkInsertCommands<TInsert>(string sql, IEnumerable<TInsert> insertParams, IDbTransaction transaction = null, int? batchSize = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			return GetBulkInsertCommands(sql, (object) null, insertParams, transaction, batchSize, cancellationToken);
		}

		/// <summary>
		/// Gets the Dapper <c>CommandDefinition</c>s used by <c>BulkInsert</c> and <c>BulkInsertAsync</c>.
		/// </summary>
		public static IEnumerable<CommandDefinition> GetBulkInsertCommands<TCommon, TInsert>(string sql, TCommon commonParam, IEnumerable<TInsert> insertParams, IDbTransaction transaction = null, int? batchSize = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (sql == null)
				throw new ArgumentNullException(nameof(sql));
			if (insertParams == null)
				throw new ArgumentNullException(nameof(insertParams));
			if (batchSize < 1)
				throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "Batch size must be positive.");

			// find VALUES clause
			var valuesClauseMatches = s_valuesClauseRegex.Matches(sql);
			if (valuesClauseMatches.Count == 0)
				throw new ArgumentException("SQL does not contain 'VALUES (' followed by ')...'.", nameof(sql));
			if (valuesClauseMatches.Count > 1)
				throw new ArgumentException("SQL contains more than one 'VALUES (' followed by ')...'.", nameof(sql));

			return YieldBulkInsertCommands(valuesClauseMatches[0], sql, commonParam, insertParams, transaction, batchSize, cancellationToken);
		}

		private static IEnumerable<CommandDefinition> YieldBulkInsertCommands<TCommon, TInsert>(Match valuesClauseMatch, string sql, TCommon commonParam, IEnumerable<TInsert> insertParams, IDbTransaction transaction = null, int? batchSize = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			// identify SQL parts
			Group tupleMatch = valuesClauseMatch.Groups[1];
			int sqlPrefixLength = tupleMatch.Index;
			int sqlSuffixIndex = valuesClauseMatch.Index + valuesClauseMatch.Length;
			string tupleSql = tupleMatch.Value;

			// get common names and values
			string[] commonNames = ParamExtractor<TCommon>.GetNames();
			object[] commonValues = ParamExtractor<TCommon>.GetValues(commonParam);

			// get insert names and find insert parameters in tuple
			string[] insertNames = ParamExtractor<TInsert>.GetNames();
			var pastEndTupleSqlIndices = s_parameterRegex.Matches(tupleSql).Cast<Match>()
				.Where(match => insertNames.Any(name => string.Compare(match.Value, 1, name, 0, match.Value.Length, StringComparison.OrdinalIgnoreCase) == 0))
				.Select(match => match.Index + match.Length)
				.ToList();

			// calculate batch size (999 is SQLite's maximum and works well with MySql.Data)
			const int maxParamsPerBatch = 999;
			int actualBatchSize = batchSize ??
				Math.Max(1, (maxParamsPerBatch - commonNames.Length) / Math.Max(1, insertNames.Length));

			// insert one batch at a time
			string batchSql = null;
			int lastBatchCount = 0;
			StringBuilder batchSqlBuilder = null;
			foreach (var insertParamBatch in EnumerateBatches(insertParams, actualBatchSize))
			{
				// build the SQL for the batch
				int batchCount = insertParamBatch.Count;
				if (batchSql == null || batchCount != lastBatchCount)
				{
					if (batchSqlBuilder == null)
						batchSqlBuilder = new StringBuilder(sqlPrefixLength + batchCount * (1 + tupleSql.Length + pastEndTupleSqlIndices.Count * 4) + (sql.Length - sqlSuffixIndex));
					else
						batchSqlBuilder.Clear();

					batchSqlBuilder.Append(sql, 0, sqlPrefixLength);

					for (int rowIndex = 0; rowIndex < batchCount; rowIndex++)
					{
						if (rowIndex != 0)
							batchSqlBuilder.Append(',');

						int tupleSqlIndex = 0;
						foreach (int pastEndTupleSqlIndex in pastEndTupleSqlIndices)
						{
							batchSqlBuilder.Append(tupleSql, tupleSqlIndex, pastEndTupleSqlIndex - tupleSqlIndex);
							batchSqlBuilder.Append('_');
							batchSqlBuilder.Append(rowIndex.ToString(CultureInfo.InvariantCulture));
							tupleSqlIndex = pastEndTupleSqlIndex;
						}
						batchSqlBuilder.Append(tupleSql, tupleSqlIndex, tupleSql.Length - tupleSqlIndex);
					}

					batchSqlBuilder.Append(sql, sqlSuffixIndex, sql.Length - sqlSuffixIndex);
					batchSql = batchSqlBuilder.ToString();
					lastBatchCount = batchCount;
				}

				// add the parameters for the batch
				var batchParameters = new DynamicParameters();
				for (int commonIndex = 0; commonIndex < commonNames.Length; commonIndex++)
					batchParameters.Add(commonNames[commonIndex], commonValues[commonIndex]);

				// enumerate rows to insert
				for (int rowIndex = 0; rowIndex < insertParamBatch.Count; rowIndex++)
				{
					var insertParam = insertParamBatch[rowIndex];
					var insertValues = ParamExtractor<TInsert>.GetValues(insertParam);
					for (int insertIndex = 0; insertIndex < insertNames.Length; insertIndex++)
						batchParameters.Add(insertNames[insertIndex] + "_" + rowIndex.ToString(CultureInfo.InvariantCulture), insertValues[insertIndex]);
				}

				// return command definition
				yield return new CommandDefinition(batchSql, batchParameters, transaction, default(int?), default(CommandType?), CommandFlags.Buffered, cancellationToken);
			}
		}

		// cache property names and getters for each type
		private static class ParamExtractor<T>
		{
			public static string[] GetNames() => s_names;

			public static object[] GetValues(T param)
			{
				var values = new object[s_getters.Length];
				if (param != null)
				{
					for (int index = 0; index < values.Length; index++)
						values[index] = s_getters[index](param);
				}
				return values;
			}

			static ParamExtractor()
			{
				var names = new List<string>();
				var getters = new List<Func<T, object>>();
				foreach (var property in typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public))
				{
					var getter = TryCreateGetter(property);
					if (getter != null)
					{
						names.Add(property.Name);
						getters.Add(getter);
					}
				}
				s_names = names.ToArray();
				s_getters = getters.ToArray();
			}

			private static Func<T, object> TryCreateGetter(PropertyInfo property)
			{
				var getMethod = property.GetGetMethod();
				var ownerType = property.DeclaringType;
				if (getMethod == null || ownerType == null)
					return null;

				var dynamicGetMethod = new DynamicMethod(name: $"_Get{property.Name}_",
					returnType: typeof(T), parameterTypes: new Type[] { typeof(object) }, owner: ownerType);
				var generator = dynamicGetMethod.GetILGenerator();
				generator.DeclareLocal(typeof(object));
				generator.Emit(OpCodes.Ldarg_0);
				generator.Emit(OpCodes.Castclass, ownerType);
				generator.EmitCall(OpCodes.Callvirt, getMethod, null);
				if (!property.PropertyType.IsClass)
					generator.Emit(OpCodes.Box, property.PropertyType);
				generator.Emit(OpCodes.Ret);

				return (Func<T, object>) dynamicGetMethod.CreateDelegate(typeof(Func<T, object>));
			}

			static readonly string[] s_names;
			static readonly Func<T, object>[] s_getters;
		}

		private static IEnumerable<IReadOnlyList<T>> EnumerateBatches<T>(IEnumerable<T> items, int batchSize)
		{
			var itemsAsList = items as IReadOnlyList<T>;
			if (itemsAsList != null)
			{
				int count = itemsAsList.Count;
				if (count <= batchSize)
					return count != 0 ? new[] { itemsAsList } : Enumerable.Empty<IReadOnlyList<T>>();
			}

			return YieldBatches(items, batchSize);
		}

		private static IEnumerable<IReadOnlyList<T>> YieldBatches<T>(IEnumerable<T> items, int batchSize)
		{
			var batch = new List<T>(batchSize);

			foreach (T item in items)
			{
				batch.Add(item);

				if (batch.Count == batchSize)
				{
					yield return batch;
					batch = new List<T>(batchSize);
				}
			}

			if (batch.Count != 0)
				yield return batch;
		}

		static readonly Regex s_valuesClauseRegex = new Regex(
			@"\b[vV][aA][lL][uU][eE][sS]\s*(\(.*?\))\s*\.\.\.", RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.RightToLeft | RegexOptions.Compiled);

		static readonly Regex s_parameterRegex = new Regex(@"[@:?]\w+\b", RegexOptions.CultureInvariant | RegexOptions.Compiled);
	}
}
