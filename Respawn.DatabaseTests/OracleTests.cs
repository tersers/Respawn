﻿using Xunit.Abstractions;

#if NET452
namespace Respawn.DatabaseTests
{
    using System;
    using System.Threading.Tasks;
    using NPoco;
    using Oracle.ManagedDataAccess.Client;
    using Shouldly;
    using Xunit;

    public class OracleTests : IAsyncLifetime
    {
        private readonly ITestOutputHelper _output;
        private OracleConnection _connection;
        private Database _database;
        private string _createdUser;
        private bool _skipTests;

        public class foo
        {
            public int value { get; set; }
        }
        public class bar
        {
            public int value { get; set; }
        }

        public OracleTests(ITestOutputHelper output)
        {
            _output = output;
            _skipTests = Environment.GetEnvironmentVariable("Appveyor")?.ToUpperInvariant() == "TRUE";
        }

        public async Task InitializeAsync()
        {
            if (_skipTests)
                return;

            _createdUser = Guid.NewGuid().ToString().Substring(0, 8);
            await CreateUser(_createdUser);

            _connection = new OracleConnection("Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=localhost)(PORT=1521))(CONNECT_DATA=(SID=xe)));User Id=\"" + _createdUser + "\";Password=123456;");
            await _connection.OpenAsync();

            _database = new Database(_connection, DatabaseType.OracleManaged);
        }

        [Fact]
        public async Task ShouldDeleteData()
        {
            if (_skipTests)
                return;

            await _database.ExecuteAsync("create table \"foo\" (value int)");

            for (int i = 0; i < 100; i++)
            {
                await _database.ExecuteAsync("INSERT INTO \"foo\" VALUES (@0)", i);
            }

            (await _database.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM \"foo\"")).ShouldBe(100);

            var checkpoint = new Checkpoint
            {
                DbAdapter = DbAdapter.Oracle,
                SchemasToInclude = new[] { _createdUser }
            };
            try
            {
                await checkpoint.Reset(_connection);
            }
            finally
            {
                _output.WriteLine(_createdUser);
                _output.WriteLine(checkpoint.DeleteSql);
            }

            (await _database.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM \"foo\"")).ShouldBe(0);
        }

        [Fact]
        public async Task ShouldDeleteMultipleTables()
        {
            if (_skipTests)
                return;

            await _database.ExecuteAsync("create table \"foo\" (value int)");
            await _database.ExecuteAsync("create table \"bar\" (value int)");

            for (int i = 0; i < 100; i++)
            {
                await _database.ExecuteAsync("INSERT INTO \"foo\" VALUES (@0)", i);
                await _database.ExecuteAsync("INSERT INTO \"bar\" VALUES (@0)", i);
            }

            var checkpoint = new Checkpoint
            {
                DbAdapter = DbAdapter.Oracle,
                SchemasToInclude = new[] { _createdUser },
            };
            await checkpoint.Reset(_connection);

            (await _database.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM \"foo\"")).ShouldBe(0);
            (await _database.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM \"bar\"")).ShouldBe(0);
        }

        [Fact]
        public async Task ShouldHandleRelationships()
        {
            if (_skipTests)
                return;

            _database.Execute("create table \"foo\" (value int, primary key (value))");
            _database.Execute("create table \"baz\" (value int, foovalue int, constraint FK_Foo foreign key (foovalue) references \"foo\" (value))");

            for (int i = 0; i < 100; i++)
            {
                _database.Execute("INSERT INTO \"foo\" VALUES (@0)", i);
                _database.Execute("INSERT INTO \"baz\" VALUES (@0, @0)", i);
            }

            _database.ExecuteScalar<int>("SELECT COUNT(1) FROM \"foo\"").ShouldBe(100);
            _database.ExecuteScalar<int>("SELECT COUNT(1) FROM \"baz\"").ShouldBe(100);

            var checkpoint = new Checkpoint
            {
                DbAdapter = DbAdapter.Oracle,
                SchemasToInclude = new[] { _createdUser },
            };
            try
            {
                await checkpoint.Reset(_connection);
            }
            catch
            {
                _output.WriteLine(checkpoint.DeleteSql ?? string.Empty);
                throw;
            }

            _database.ExecuteScalar<int>("SELECT COUNT(1) FROM \"foo\"").ShouldBe(0);
            _database.ExecuteScalar<int>("SELECT COUNT(1) FROM \"baz\"").ShouldBe(0);
        }

        [Fact]
        public async Task ShouldHandleCircularRelationships()
        {
            if (_skipTests)
                return;

            _database.Execute("create table \"parent\" (id int primary key, childid int NULL)");
            _database.Execute("create table \"child\" (id int primary key, parentid int NULL)");
            _database.Execute("alter table \"parent\" add constraint FK_Child foreign key (ChildId) references \"child\" (Id)");
            _database.Execute("alter table \"child\" add constraint FK_Parent foreign key (ParentId) references \"parent\" (Id)");

            for (int i = 0; i < 100; i++)
            {
                _database.Execute("INSERT INTO \"parent\" VALUES (@0, null)", i);
                _database.Execute("INSERT INTO \"child\" VALUES (@0, null)", i);
            }

            _database.Execute("update \"parent\" set childid = 0");
            _database.Execute("update \"child\" set parentid = 1");

            _database.ExecuteScalar<int>("SELECT COUNT(1) FROM \"parent\"").ShouldBe(100);
            _database.ExecuteScalar<int>("SELECT COUNT(1) FROM \"child\"").ShouldBe(100);

            var checkpoint = new Checkpoint
            {
                DbAdapter = DbAdapter.Oracle,
                SchemasToInclude = new[] { _createdUser },
            };
            try
            {
                await checkpoint.Reset(_connection);
            }
            catch
            {
                _output.WriteLine(checkpoint.DeleteSql ?? string.Empty);
                throw;
            }

            _database.ExecuteScalar<int>("SELECT COUNT(1) FROM \"parent\"").ShouldBe(0);
            _database.ExecuteScalar<int>("SELECT COUNT(1) FROM \"child\"").ShouldBe(0);
        }

        [Fact]
        public async Task ShouldIgnoreTables()
        {
            if (_skipTests)
                return;

            await _database.ExecuteAsync("create table \"foo\" (value int)");
            await _database.ExecuteAsync("create table \"bar\" (value int)");

            for (int i = 0; i < 100; i++)
            {
                await _database.ExecuteAsync("INSERT INTO \"foo\" VALUES (@0)", i);
                await _database.ExecuteAsync("INSERT INTO \"bar\" VALUES (@0)", i);
            }

            var checkpoint = new Checkpoint
            {
                DbAdapter = DbAdapter.Oracle,
                SchemasToInclude = new[] { _createdUser },
                TablesToIgnore = new[] { "foo" }
            };
            await checkpoint.Reset(_connection);

            (await _database.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM \"foo\"")).ShouldBe(100);
            (await _database.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM \"bar\"")).ShouldBe(0);
        }

        [Fact]
        public async Task ShouldExcludeSchemas()
        {
            if (_skipTests)
                return;

            var userA = Guid.NewGuid().ToString().Substring(0, 8);
            var userB = Guid.NewGuid().ToString().Substring(0, 8);
            await CreateUser(userA);
            await CreateUser(userB);
            await _database.ExecuteAsync("create table \"" + userA + "\".\"foo\" (value int)");
            await _database.ExecuteAsync("create table \"" + userB + "\".\"bar\" (value int)");

            for (int i = 0; i < 100; i++)
            {
                await _database.ExecuteAsync("INSERT INTO \"" + userA + "\".\"foo\" VALUES (" + i + ")");
                await _database.ExecuteAsync("INSERT INTO \"" + userB + "\".\"bar\" VALUES (" + i + ")");
            }

            var checkpoint = new Checkpoint
            {
                // We must make sure we don't delete all these users that are used by Oracle
                DbAdapter = DbAdapter.Oracle,
                SchemasToExclude = new[]
                {
                    userA, "ANONYMOUS", "APEX_040000", "APEX_PUBLIC_USER", "APPQOSSYS",
                    "CTXSYS", "DBSNMP", "DIP", "FLOWS_FILES", "HR", "MDSYS",
                    "ORACLE_OCM", "OUTLN", "SYS", "XDB", "XS$NULL", "SYSTEM",
                    "GSMADMIN_INTERNAL", "WMSYS", "OJVMSYS", "ORDSYS", "ORDDATA",
                    "LBACSYS", "APEX_040200", "DVSYS", "AUDSYS", "OLAPSYS", "SCOTT"
                }
            };
            await checkpoint.Reset(_connection);

            (await _database.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM \"" + userA + "\".\"foo\"")).ShouldBe(100);
            (await _database.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM \"" + userB + "\".\"bar\"")).ShouldBe(0);

            // Clean up before leaving
            await DropUser(userA);
            await DropUser(userB);
        }

        [Fact]
        public async Task ShouldIncludeSchemas()
        {
            if (_skipTests)
                return;

            var userA = Guid.NewGuid().ToString().Substring(0, 8);
            var userB = Guid.NewGuid().ToString().Substring(0, 8);
            await CreateUser(userA);
            await CreateUser(userB);
            await _database.ExecuteAsync("create table \"" + userA + "\".\"foo\" (value int)");
            await _database.ExecuteAsync("create table \"" + userB + "\".\"bar\" (value int)");

            for (int i = 0; i < 100; i++)
            {
                await _database.ExecuteAsync("INSERT INTO \"" + userA + "\".\"foo\" VALUES (" + i + ")");
                await _database.ExecuteAsync("INSERT INTO \"" + userB + "\".\"bar\" VALUES (" + i + ")");
            }

            var checkpoint = new Checkpoint
            {
                DbAdapter = DbAdapter.Oracle,
                SchemasToInclude = new[] { userB }
            };
            await checkpoint.Reset(_connection);

            (await _database.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM \"" + userA + "\".\"foo\"")).ShouldBe(100);
            (await _database.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM \"" + userB + "\".\"bar\"")).ShouldBe(0);

            // Clean up before leaving
            await DropUser(userA);
            await DropUser(userB);
        }

        private static async Task CreateUser(string userName)
        {
            using (var connection = new OracleConnection("Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=localhost)(PORT=1521))(CONNECT_DATA=(SID=xe)));User Id=system;Password=oracle;"))
            {
                await connection.OpenAsync();

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "create user \"" + userName + "\" IDENTIFIED BY 123456";
                    await cmd.ExecuteNonQueryAsync();
                    // We need some permissions in order to execute all the test queries
                    cmd.CommandText = "alter user \"" + userName + "\" IDENTIFIED BY 123456 account unlock";
                    await cmd.ExecuteNonQueryAsync();
                    cmd.CommandText = "grant all privileges to \"" + userName + "\" IDENTIFIED BY 123456";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }

        private static async Task DropUser(string userName)
        {
            using (var connection = new OracleConnection("Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=localhost)(PORT=1521))(CONNECT_DATA=(SID=xe)));User Id=system;Password=oracle;"))
            {
                await connection.OpenAsync();

                using (var cmd = connection.CreateCommand())
                {
                    // First we need to disconnect the user
                    cmd.CommandText = @"SELECT s.sid, s.serial#, s.status, p.spid FROM v$session s, v$process p WHERE s.username = '" + userName + "' AND p.addr(+) = s.paddr";

                    var dataReader = cmd.ExecuteReader();
                    if (await dataReader.ReadAsync())
                    {
                        var sid = dataReader.GetOracleDecimal(0);
                        var serial = dataReader.GetOracleDecimal(1);

                        cmd.CommandText = "ALTER SYSTEM KILL SESSION '" + sid + ", " + serial + "'";
                        await cmd.ExecuteNonQueryAsync();
                    }

                    cmd.CommandText = "drop user \"" + userName + "\" CASCADE";
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }

        public async Task DisposeAsync()
        {
            if (_skipTests)
                return;
         
            // Clean up our mess before leaving
            await DropUser(_createdUser);

            _connection.Close();
            _connection.Dispose();
            _connection = null;

            _database.Dispose();
            _database = null;
        }
    }
}
#endif