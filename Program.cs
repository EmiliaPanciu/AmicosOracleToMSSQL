using Oracle.ManagedDataAccess.Client;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Text;

namespace OracleToMSSQLMigrator
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("Oracle to MSSQL Database Migration Tool");
            Console.WriteLine("========================================\n");

            // Get connection strings from user or configuration
            string oracleConnectionString = GetOracleConnectionString();
            string mssqlConnectionString = GetMSSQLConnectionString();

            if (string.IsNullOrEmpty(oracleConnectionString) || string.IsNullOrEmpty(mssqlConnectionString))
            {
                Console.WriteLine("Connection strings are required. Please set up configuration.");
                return;
            }

            try
            {
                var migrator = new DatabaseMigrator(oracleConnectionString, mssqlConnectionString);
                await migrator.MigrateAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during migration: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }
        }

        static string GetOracleConnectionString()
        {
            // Try to get from environment variable first
            string? connectionString = Environment.GetEnvironmentVariable("ORACLE_CONNECTION_STRING");
            
            if (string.IsNullOrEmpty(connectionString))
            {
                // Try to get from appsettings.json or ask user
                Console.WriteLine("Enter Oracle connection string (or press Enter to use environment variable ORACLE_CONNECTION_STRING):");
                Console.WriteLine("Format: User Id=username;Password=password;Data Source=hostname:port/servicename");
                connectionString = Console.ReadLine();
            }

            return connectionString ?? "";
        }

        static string GetMSSQLConnectionString()
        {
            // Try to get from environment variable first
            string? connectionString = Environment.GetEnvironmentVariable("MSSQL_CONNECTION_STRING");
            
            if (string.IsNullOrEmpty(connectionString))
            {
                // Try to get from appsettings.json or ask user
                Console.WriteLine("Enter MSSQL connection string (or press Enter to use environment variable MSSQL_CONNECTION_STRING):");
                Console.WriteLine("Format: Server=hostname;Database=database;User Id=username;Password=password;TrustServerCertificate=True");
                connectionString = Console.ReadLine();
            }

            return connectionString ?? "";
        }
    }

    public class DatabaseMigrator
    {
        private readonly string _oracleConnectionString;
        private readonly string _mssqlConnectionString;

        public DatabaseMigrator(string oracleConnectionString, string mssqlConnectionString)
        {
            _oracleConnectionString = oracleConnectionString;
            _mssqlConnectionString = mssqlConnectionString;
        }

        public async Task MigrateAsync()
        {
            Console.WriteLine("Starting migration process...\n");

            // Get all tables from Oracle
            var tables = await GetOracleTablesAsync();
            Console.WriteLine($"Found {tables.Count} tables in Oracle database.\n");

            foreach (var tableName in tables)
            {
                Console.WriteLine($"Processing table: {tableName}");
                try
                {
                    // Get table schema from Oracle
                    var schema = await GetTableSchemaAsync(tableName);
                    
                    // Create table in MSSQL (without foreign keys)
                    await CreateMSSQLTableAsync(tableName, schema);
                    
                    // Copy data from Oracle to MSSQL
                    await CopyDataAsync(tableName, schema);
                    
                    Console.WriteLine($"✓ Successfully migrated table: {tableName}\n");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"✗ Error migrating table {tableName}: {ex.Message}\n");
                }
            }

            Console.WriteLine("Migration completed!");
        }

        private async Task<List<string>> GetOracleTablesAsync()
        {
            var tables = new List<string>();
            
            using var connection = new OracleConnection(_oracleConnectionString);
            await connection.OpenAsync();
            
            // Query to get all user tables (excluding system tables)
            string query = @"
                SELECT table_name 
                FROM user_tables 
                ORDER BY table_name";
            
            using var command = new OracleCommand(query, connection);
            using var reader = await command.ExecuteReaderAsync();
            
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
            
            return tables;
        }

        private async Task<List<ColumnSchema>> GetTableSchemaAsync(string tableName)
        {
            var columns = new List<ColumnSchema>();
            
            using var connection = new OracleConnection(_oracleConnectionString);
            await connection.OpenAsync();
            
            // Query to get column information, excluding foreign key constraints
            string query = @"
                SELECT 
                    c.column_name,
                    c.data_type,
                    c.data_length,
                    c.data_precision,
                    c.data_scale,
                    c.nullable,
                    CASE WHEN pk.column_name IS NOT NULL THEN 'Y' ELSE 'N' END as is_primary_key
                FROM user_tab_columns c
                LEFT JOIN (
                    SELECT cols.column_name
                    FROM user_constraints cons
                    JOIN user_cons_columns cols ON cons.constraint_name = cols.constraint_name
                    WHERE cons.constraint_type = 'P' AND cons.table_name = :tableName
                ) pk ON c.column_name = pk.column_name
                WHERE c.table_name = :tableName
                ORDER BY c.column_id";
            
            using var command = new OracleCommand(query, connection);
            command.Parameters.Add(new OracleParameter("tableName", tableName));
            
            using var reader = await command.ExecuteReaderAsync();
            
            while (await reader.ReadAsync())
            {
                columns.Add(new ColumnSchema
                {
                    Name = reader.GetString(0),
                    DataType = reader.GetString(1),
                    Length = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                    Precision = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                    Scale = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                    IsNullable = reader.GetString(5) == "Y",
                    IsPrimaryKey = reader.GetString(6) == "Y"
                });
            }
            
            return columns;
        }

        private async Task CreateMSSQLTableAsync(string tableName, List<ColumnSchema> columns)
        {
            using var connection = new SqlConnection(_mssqlConnectionString);
            await connection.OpenAsync();
            
            // Drop table if exists
            string dropQuery = $"IF OBJECT_ID('{tableName}', 'U') IS NOT NULL DROP TABLE [{tableName}]";
            using (var dropCommand = new SqlCommand(dropQuery, connection))
            {
                await dropCommand.ExecuteNonQueryAsync();
            }
            
            // Build CREATE TABLE statement
            var sb = new StringBuilder();
            sb.AppendLine($"CREATE TABLE [{tableName}] (");
            
            var columnDefinitions = new List<string>();
            var primaryKeys = new List<string>();
            
            foreach (var column in columns)
            {
                string sqlType = ConvertOracleTypeToMSSQL(column);
                string nullable = column.IsNullable ? "NULL" : "NOT NULL";
                columnDefinitions.Add($"    [{column.Name}] {sqlType} {nullable}");
                
                if (column.IsPrimaryKey)
                {
                    primaryKeys.Add(column.Name);
                }
            }
            
            sb.AppendLine(string.Join(",\n", columnDefinitions));
            
            // Add primary key constraint (but NOT foreign keys)
            if (primaryKeys.Count > 0)
            {
                sb.AppendLine($",    CONSTRAINT [PK_{tableName}] PRIMARY KEY ({string.Join(", ", primaryKeys.Select(pk => $"[{pk}]"))})");
            }
            
            sb.AppendLine(")");
            
            string createQuery = sb.ToString();
            using var command = new SqlCommand(createQuery, connection);
            await command.ExecuteNonQueryAsync();
            
            Console.WriteLine($"  Created table structure");
        }

        private string ConvertOracleTypeToMSSQL(ColumnSchema column)
        {
            return column.DataType.ToUpper() switch
            {
                "VARCHAR2" => $"NVARCHAR({(column.Length > 0 ? column.Length : 255)})",
                "NVARCHAR2" => $"NVARCHAR({(column.Length > 0 ? column.Length : 255)})",
                "CHAR" => $"NCHAR({(column.Length > 0 ? column.Length : 1)})",
                "NCHAR" => $"NCHAR({(column.Length > 0 ? column.Length : 1)})",
                "NUMBER" when column.Scale > 0 => $"DECIMAL({column.Precision}, {column.Scale})",
                "NUMBER" when column.Precision > 0 && column.Scale == 0 => column.Precision <= 9 ? "INT" : "BIGINT",
                "NUMBER" => "DECIMAL(38, 10)",
                "INTEGER" => "INT",
                "INT" => "INT",
                "SMALLINT" => "SMALLINT",
                "FLOAT" => "FLOAT",
                "REAL" => "REAL",
                "DOUBLE PRECISION" => "FLOAT",
                "DATE" => "DATETIME2",
                "TIMESTAMP" => "DATETIME2",
                "TIMESTAMP(6)" => "DATETIME2",
                "TIMESTAMP WITH TIME ZONE" => "DATETIMEOFFSET",
                "TIMESTAMP WITH LOCAL TIME ZONE" => "DATETIMEOFFSET",
                "CLOB" => "NVARCHAR(MAX)",
                "NCLOB" => "NVARCHAR(MAX)",
                "BLOB" => "VARBINARY(MAX)",
                "RAW" => $"VARBINARY({(column.Length > 0 ? column.Length : 255)})",
                "LONG" => "NVARCHAR(MAX)",
                "LONG RAW" => "VARBINARY(MAX)",
                _ => "NVARCHAR(255)"
            };
        }

        private async Task CopyDataAsync(string tableName, List<ColumnSchema> columns)
        {
            using var oracleConnection = new OracleConnection(_oracleConnectionString);
            using var mssqlConnection = new SqlConnection(_mssqlConnectionString);
            
            await oracleConnection.OpenAsync();
            await mssqlConnection.OpenAsync();
            
            // Read data from Oracle
            string selectQuery = $"SELECT * FROM {tableName}";
            using var oracleCommand = new OracleCommand(selectQuery, oracleConnection);
            using var reader = await oracleCommand.ExecuteReaderAsync();
            
            int rowCount = 0;
            int batchSize = 1000;
            var batch = new List<object[]>();
            
            while (await reader.ReadAsync())
            {
                var row = new object[columns.Count];
                for (int i = 0; i < columns.Count; i++)
                {
                    row[i] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
                }
                batch.Add(row);
                rowCount++;
                
                // Insert in batches
                if (batch.Count >= batchSize)
                {
                    await InsertBatchAsync(mssqlConnection, tableName, columns, batch);
                    batch.Clear();
                }
            }
            
            // Insert remaining rows
            if (batch.Count > 0)
            {
                await InsertBatchAsync(mssqlConnection, tableName, columns, batch);
            }
            
            Console.WriteLine($"  Copied {rowCount} rows");
        }

        private async Task InsertBatchAsync(SqlConnection connection, string tableName, List<ColumnSchema> columns, List<object[]> batch)
        {
            using var transaction = connection.BeginTransaction();
            
            try
            {
                // Build query once for all rows in batch
                var columnNames = string.Join(", ", columns.Select(c => $"[{c.Name}]"));
                var parameters = string.Join(", ", columns.Select((c, i) => $"@p{i}"));
                string insertQuery = $"INSERT INTO [{tableName}] ({columnNames}) VALUES ({parameters})";
                
                foreach (var row in batch)
                {
                    using var command = new SqlCommand(insertQuery, connection, transaction);
                    
                    for (int i = 0; i < columns.Count; i++)
                    {
                        command.Parameters.AddWithValue($"@p{i}", row[i]);
                    }
                    
                    await command.ExecuteNonQueryAsync();
                }
                
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
    }

    public class ColumnSchema
    {
        public string Name { get; set; } = "";
        public string DataType { get; set; } = "";
        public int Length { get; set; }
        public int Precision { get; set; }
        public int Scale { get; set; }
        public bool IsNullable { get; set; }
        public bool IsPrimaryKey { get; set; }
    }
}
