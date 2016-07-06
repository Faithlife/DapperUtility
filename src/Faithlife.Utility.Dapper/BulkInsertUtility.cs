using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Reflection;
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

		// cache property list for each anonymous type
		private static class ParamExtractor<T>
		{
			public static string[] GetNames()
			{
				var names = new string[s_properties.Length];
				for (int index = 0; index < names.Length; index++)
					names[index] = s_properties[index].Name;
				return names;
			}

			public static object[] GetValues(T param)
			{
				var values = new object[s_properties.Length];
				if (param != null)
				{
					for (int index = 0; index < values.Length; index++)
						values[index] = s_properties[index].GetValue(param);
				}
				return values;
			}

			static readonly PropertyInfo[] s_properties = typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public);
		}

		private static IEnumerable<ReadOnlyCollection<T>> EnumerateBatches<T>(IEnumerable<T> items, int batchSize)
		{
			if (items == null)
				throw new ArgumentNullException(nameof(items));
			if (batchSize < 1)
				throw new ArgumentOutOfRangeException(nameof(batchSize));

			var itemsAsList = items as IList<T>;
			if (itemsAsList != null)
			{
				int count = itemsAsList.Count;
				if (count <= batchSize)
					return count != 0 ? new[] { new ReadOnlyCollection<T>(itemsAsList) } : Enumerable.Empty<ReadOnlyCollection<T>>();
			}

			return YieldBatches(items, batchSize);
		}

		private static IEnumerable<ReadOnlyCollection<T>> YieldBatches<T>(IEnumerable<T> items, int batchSize)
		{
			var batch = new List<T>(batchSize);

			foreach (T item in items)
			{
				batch.Add(item);

				if (batch.Count == batchSize)
				{
					yield return batch.AsReadOnly();
					batch = new List<T>(batchSize);
				}
			}

			if (batch.Count != 0)
				yield return batch.AsReadOnly();
		}

		static readonly Regex s_valuesClauseRegex = new Regex(
			@"\b[vV][aA][lL][uU][eE][sS]\s*(\(.*?\))\s*\.\.\.", RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.RightToLeft);

		static readonly Regex s_parameterRegex = new Regex(@"[@:?]\w+\b", RegexOptions.CultureInvariant);
	}
}
