using NUnit.Framework;

namespace Faithlife.Utility.Dapper.Tests
{
	internal static class OurTestUtility
	{
		public static void ShouldBe<T>(this T actual, T expected)
		{
			Assert.AreEqual(expected, actual);
		}
	}
}
