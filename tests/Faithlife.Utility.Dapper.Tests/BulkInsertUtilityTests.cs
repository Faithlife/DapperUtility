using Dapper;
using FluentAssertions;
using Xunit;

namespace Faithlife.Utility.Dapper.Tests
{
	public class BulkInsertUtilityTests
	{
		[Fact]
		public void NullSql_Throws()
		{
			Assert.Throws<ArgumentNullException>(() =>
			{
				BulkInsertUtility.GetBulkInsertCommands(null, new[] { new { foo = 1 } });
			});
		}

		[Fact]
		public void EmptySql_Throws()
		{
			Assert.Throws<ArgumentException>(() =>
			{
				BulkInsertUtility.GetBulkInsertCommands("", new[] { new { foo = 1 } });
			});
		}

		[Fact]
		public void NullInsertParams_Throws()
		{
			Assert.Throws<ArgumentNullException>(() =>
			{
				BulkInsertUtility.GetBulkInsertCommands("VALUES (@foo)...", default(object[]));
			});
		}

		[Fact]
		public void NoValues_Throws()
		{
			Assert.Throws<ArgumentException>(() =>
			{
				BulkInsertUtility.GetBulkInsertCommands("VALUE (@foo)...", new[] { new { foo = 1 } });
			});
		}

		[Fact]
		public void ValuesSuffix_Throws()
		{
			Assert.Throws<ArgumentException>(() =>
			{
				BulkInsertUtility.GetBulkInsertCommands("1VALUES (@foo)...", new[] { new { foo = 1 } });
			});
		}

		[Fact]
		public void NoEllipsis_Throws()
		{
			Assert.Throws<ArgumentException>(() =>
			{
				BulkInsertUtility.GetBulkInsertCommands("VALUE (@foo)..", new[] { new { foo = 1 } });
			});
		}

		[Fact]
		public void MultipleValues_Throws()
		{
			Assert.Throws<ArgumentException>(() =>
			{
				BulkInsertUtility.GetBulkInsertCommands("VALUES (@foo)... VALUES (@foo)...", new[] { new { foo = 1 } });
			});
		}

		[Fact]
		public void NegativeBatchSize_Throws()
		{
			Assert.Throws<ArgumentOutOfRangeException>(() =>
			{
				BulkInsertUtility.GetBulkInsertCommands("VALUES (@foo)...", new[] { new { foo = 1 } }, batchSize: -1);
			});
		}

		[Fact]
		public void MinimalInsert()
		{
			var commands = BulkInsertUtility.GetBulkInsertCommands("INSERT INTO t (foo)VALUES(@foo)...;", new[] { new { foo = 1 } }).ToList();
			commands.Count.Should().Be(1);
			commands[0].CommandText.Should().Be("INSERT INTO t (foo)VALUES(@foo_0);");
			var parameters = (DynamicParameters) commands[0].Parameters;
			parameters.ParameterNames.Single().Should().Be("foo_0");
			((SqlMapper.IParameterLookup) parameters)["foo_0"].Should().Be(1);
		}

		[Fact]
		public void InsertNotRequired()
		{
			var commands = BulkInsertUtility.GetBulkInsertCommands("VALUES (@foo)...", new[] { new { foo = 1 } }).ToList();
			commands.Count.Should().Be(1);
			commands[0].CommandText.Should().Be("VALUES (@foo_0)");
		}

		[Fact]
		public void MultipleInserts()
		{
			var commands = BulkInsertUtility.GetBulkInsertCommands("INSERT INTO t VALUES (@t); INSERT INTO u VALUES (@u)...; INSERT INTO v VALUES (@v);",
				new[] { new { t = 1, u = 2, v = 3 } }).ToList();
			commands.Count.Should().Be(1);
			commands[0].CommandText.Should().Be("INSERT INTO t VALUES (@t); INSERT INTO u VALUES (@u_0); INSERT INTO v VALUES (@v);");
		}

		[Fact]
		public void CommonAndInsertedParameters()
		{
			var commands = BulkInsertUtility.GetBulkInsertCommands("VALUES (@a, @b, @c, @d)...", new { a = 1, b = 2 }, new[] { new { c = 3, d = 4 }, new { c = 5, d = 6 } }).ToList();
			commands.Count.Should().Be(1);
			commands[0].CommandText.Should().Be("VALUES (@a, @b, @c_0, @d_0),(@a, @b, @c_1, @d_1)");
			var parameters = (SqlMapper.IParameterLookup) commands[0].Parameters;
			parameters["a"].Should().Be(1);
			parameters["b"].Should().Be(2);
			parameters["c_0"].Should().Be(3);
			parameters["d_0"].Should().Be(4);
			parameters["c_1"].Should().Be(5);
			parameters["d_1"].Should().Be(6);
		}

		[Fact]
		public void EightRowsInThreeBatches()
		{
			var commands = BulkInsertUtility.GetBulkInsertCommands("VALUES(@foo)...", Enumerable.Range(0, 8).Select(x => new { foo = x }), batchSize: 3).ToList();
			commands.Count.Should().Be(3);
			commands[0].CommandText.Should().Be("VALUES(@foo_0),(@foo_1),(@foo_2)");
			commands[1].CommandText.Should().Be("VALUES(@foo_0),(@foo_1),(@foo_2)");
			commands[2].CommandText.Should().Be("VALUES(@foo_0),(@foo_1)");
			((SqlMapper.IParameterLookup) commands[2].Parameters)["foo_1"].Should().Be(7);
		}

		[Fact]
		public void CaseInsensitiveValues()
		{
			var commands = BulkInsertUtility.GetBulkInsertCommands("VaLueS(@foo)...", new[] { new { foo = 1 } }).ToList();
			commands.Count.Should().Be(1);
			commands[0].CommandText.Should().Be("VaLueS(@foo_0)");
		}

		[Fact]
		public void CaseInsensitiveNames()
		{
			var commands = BulkInsertUtility.GetBulkInsertCommands("values (@foo, @Bar, @BAZ, @bam)...", new[] { new { Foo = 1, BAR = 2, baz = 3 } }).ToList();
			commands.Count.Should().Be(1);
			commands[0].CommandText.Should().Be("values (@foo_0, @Bar_0, @BAZ_0, @bam)");
		}

		[Fact]
		public void SubstringNames()
		{
			var commands = BulkInsertUtility.GetBulkInsertCommands("values (@a, @aa, @aaa, @aaaa)...", new { a = 1, aaa = 3 }, new[] { new { aa = 2, aaaa = 4 } }).ToList();
			commands.Count.Should().Be(1);
			commands[0].CommandText.Should().Be("values (@a, @aa_0, @aaa, @aaaa_0)");
		}

		[Fact]
		public void WhitespaceEverywhere()
		{
			var commands = BulkInsertUtility.GetBulkInsertCommands("\r\n\t VALUES\n\t \r(\t \r\n@foo \r\n\t)\r\n\t ...\t\r\n", new[] { new { foo = 1 } }).ToList();
			commands.Count.Should().Be(1);
			commands[0].CommandText.Should().Be("\r\n\t VALUES\n\t \r(\t \r\n@foo_0 \r\n\t)\t\r\n");
		}

		[Fact]
		public void NothingToInsert()
		{
			var commands = BulkInsertUtility.GetBulkInsertCommands("VALUES(@foo)...", Array.Empty<object>()).ToList();
			commands.Count.Should().Be(0);
		}

		[Fact]
		public void NoParameterNameValidation()
		{
			var commands = BulkInsertUtility.GetBulkInsertCommands("VALUES (@a, @b, @c, @d)...", new { e = 1, f = 2 }, new[] { new { g = 3, h = 4 }, new { g = 5, h = 6 } }).ToList();
			commands.Count.Should().Be(1);
			commands[0].CommandText.Should().Be("VALUES (@a, @b, @c, @d),(@a, @b, @c, @d)");
			var parameters = (SqlMapper.IParameterLookup) commands[0].Parameters;
			parameters["e"].Should().Be(1);
			parameters["f"].Should().Be(2);
			parameters["g_0"].Should().Be(3);
			parameters["h_0"].Should().Be(4);
			parameters["g_1"].Should().Be(5);
			parameters["h_1"].Should().Be(6);
		}

		[Fact]
		public void ComplexValues()
		{
			var commands = BulkInsertUtility.GetBulkInsertCommands("VALUES (@a + (@d * @c) -\r\n\t@d)...", new { a = 1, b = 2 }, new[] { new { c = 3, d = 4 }, new { c = 5, d = 6 } }).ToList();
			commands.Count.Should().Be(1);
			commands[0].CommandText.Should().Be("VALUES (@a + (@d_0 * @c_0) -\r\n\t@d_0),(@a + (@d_1 * @c_1) -\r\n\t@d_1)");
			var parameters = (SqlMapper.IParameterLookup) commands[0].Parameters;
			parameters["a"].Should().Be(1);
			parameters["b"].Should().Be(2);
			parameters["c_0"].Should().Be(3);
			parameters["d_0"].Should().Be(4);
			parameters["c_1"].Should().Be(5);
			parameters["d_1"].Should().Be(6);
		}
	}
}
