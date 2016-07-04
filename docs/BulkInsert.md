# BulkInsert

The `BulkInsert` (and `BulkInsertAsync`) extension methods allow efficient insertion of many rows into a database table with a familiar Dapper-like API.

## Problem

Dapper already has a mechanism for "bulk insert". Calling `Execute` with an `IEnumerable` will execute the specified `INSERT` command once for each item in the sequence. Unfortunately, this can be an extremely slow way to insert a large number of rows into a database.

Each DBMS has its own preferred approaches for efficiently inserting many rows into a database, but the most portable way is to execute an `INSERT` command with multiple rows in the `VALUES` clause, like so:

```
INSERT INTO widgets (name, size) VALUES ('foo', 22), ('bar', 14), ('baz', 42)
```

Building a SQL statement for a large number of rows is straightforward, but runs the risk of SQL injection problems if the SQL isn't escaped propertly.

Using command parameters is safer, but building and executing the SQL is more complex. Furthermore, databases often have a limit on the maximum number of command parameters that can be used, so it can be necessary to execute multiple SQL statements, one for each batch of rows to insert.

## Solution

`BulkInsert` is a simple Dapper-like extension method that builds the SQL commands for each batch and leverages Dapper to inject the command parameters.

```csharp
var widgets = new[] { new { name = "foo", size = 22 }, new { name = "bar", size = 14 }, new { name = "baz", size = 42 } };
connection.BulkInsert("INSERT INTO widgets (name, size) VALUES (@name, @size) ...", widgets);
```

The `...` after the `VALUES` clause must be included. It is used by `BulkInsert` to find the end of the `VALUES` clause that will be transformed. The call above will build a SQL statement like so:

```
INSERT INTO widgets (name, size) VALUES (@name_0, @size_0), (@name_1, @size_1)
```

The actual SQL statement will have as many parameters as needed to insert all of the specified rows. If the total number of command parameters exceeds 999 (the maximum number that [SQLite](https://www.sqlite.org/) supports and an efficient number for [MySql.Data](https://www.nuget.org/packages/MySql.Data/)), it will execute multiple SQL commands until all of the rows are inserted.

All of the transformed SQL will be executed for each batch, so including additional statements before or after the `INSERT` statement is not recommended.

Execute the method within a transaction if it is important to avoid inserting only some of the rows if there is an error.

## Reference

The `BulkInsert` and `BulkInsertAsync` methods of the `BulkInsertUtility` static class are extension methods on `IDbConnection`. They support these arguments:

* `sql` – The SQL to execute, which must have a `VALUES` clause that is followed by `...`.
* `commonParam` – The values of any command parameters that are outside the `VALUES` clause, or are common to every row. (Optional.)
* `insertParams` – The values of the command parameters for each row to be inserted.
* `transaction` – The current transaction, if any (see `Dapper.Execute`).
* `batchSize` – If specified, indicates the number of rows to insert in each batch, even if doing so requires more than 999 command parameters.
* `cancellationToken` – The optional cancellation token (`BulkInsertAsync` only).

The method returns the total number of rows affected (or, more specifically, the sum of the numbers returned when executing the SQL commands for each batch).
