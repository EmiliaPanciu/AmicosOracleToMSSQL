using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Oracle.ManagedDataAccess.Client;
using System.Data;
using System.Diagnostics;

const string defaultSchema = "";

IConfiguration configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .Build();

string oracleConnectionString = configuration["Oracle:ConnectionString"] ?? string.Empty;
string oracleSchema = configuration["Oracle:Schema"] ?? defaultSchema;
string sqlConnectionString = configuration["SqlServer:ConnectionString"] ?? string.Empty;

if (string.IsNullOrWhiteSpace(oracleConnectionString) || string.IsNullOrWhiteSpace(sqlConnectionString))
{
    Console.WriteLine("Please set Oracle:ConnectionString and SqlServer:ConnectionString in appsettings.json.");
    return;
}

using var oracleConnection = new OracleConnection(oracleConnectionString);
using var sqlConnection = new SqlConnection(sqlConnectionString);

oracleConnection.Open();
sqlConnection.Open();

string resolvedSchema = string.IsNullOrWhiteSpace(oracleSchema)
    ? oracleConnection.GetSchema("Schema").Rows[0]["USERNAME"].ToString() ?? string.Empty
    : oracleSchema.ToUpperInvariant();

var tables = LoadOracleTables(oracleConnection, resolvedSchema);

foreach (var table in tables)
{
    Console.WriteLine($"Processing {table.Schema}.{table.Name}...");

    var columns = LoadOracleColumns(oracleConnection, table.Schema, table.Name);
    CreateSqlServerTable(sqlConnection, table.Name, columns);
    TransferData(oracleConnection, sqlConnection, table.Schema, table.Name, columns);
}

Console.WriteLine("Migration completed.");

static List<OracleTable> LoadOracleTables(OracleConnection connection, string schema)
{
    const string query = """
        SELECT TABLE_NAME
        FROM ALL_TABLES
        WHERE OWNER = :schemaName
        ORDER BY TABLE_NAME
        """;

    using var command = new OracleCommand(query, connection);
    command.BindByName = true;  // Add this line
    command.Parameters.Add(new OracleParameter("schemaName", schema));

    using var reader = command.ExecuteReader();
    var tables = new List<OracleTable>();
    while (reader.Read())
    {
        var tbName = reader.GetString(0);
        if (tbName.StartsWith("STATOP")) { continue; }
        if (tbName.StartsWith("STATRE")) { continue; }
        if (tbName.StartsWith("STATRO")) { continue; }
        if (tbName.StartsWith("STATT")) { continue; }
        if (!tbName.StartsWith("TASK")) { continue; }
        //if (tbName =="TASKREG") { continue; }

        tables.Add(new OracleTable(schema, reader.GetString(0)));
    }

    return tables;
}

static List<OracleColumn> LoadOracleColumns(OracleConnection connection, string schema, string tableName)
{
    const string query = """
        SELECT COLUMN_NAME,
               DATA_TYPE,
               DATA_LENGTH,
               DATA_PRECISION,
               DATA_SCALE,
               NULLABLE
        FROM ALL_TAB_COLUMNS
        WHERE OWNER = :schemaName
          AND TABLE_NAME = :tableName
        ORDER BY COLUMN_ID
        """;

    using var command = new OracleCommand(query, connection);
    command.BindByName = true;  // Add this line
    command.Parameters.Add(new OracleParameter("schemaName", schema));
    command.Parameters.Add(new OracleParameter("tableName", tableName));

    using var reader = command.ExecuteReader();
    var columns = new List<OracleColumn>();

    while (reader.Read())
    {
        columns.Add(new OracleColumn(
            Name: reader.GetString(0),
            DataType: reader.GetString(1),
            DataLength: reader.IsDBNull(2) ? null : reader.GetInt32(2),
            DataPrecision: reader.IsDBNull(3) ? null : reader.GetInt32(3),
            DataScale: reader.IsDBNull(4) ? null : reader.GetInt32(4),
            Nullable: string.Equals(reader.GetString(5), "Y", StringComparison.OrdinalIgnoreCase))
        );
    }

    return columns;
}

static void CreateSqlServerTable(SqlConnection sqlConnection, string tableName, List<OracleColumn> columns)
{
    string columnDefinitions = string.Join(",\n",
        columns.Select(column => $"    [{column.Name}] {MapOracleTypeToSql(column)} {(column.Nullable ? "NULL" : "NOT NULL")}"));

    string createTableSql = $"""
        IF OBJECT_ID(N'[dbo].[{tableName}]', 'U') IS NULL
        BEGIN
        CREATE TABLE [dbo].[{tableName}]
        (
        {columnDefinitions}
        );
        END
        """;

    using var command = new SqlCommand(createTableSql, sqlConnection);
    command.ExecuteNonQuery();
}

static string MapOracleTypeToSql(OracleColumn column)
{
    string oracleType = column.DataType.ToUpperInvariant();

    return oracleType switch
    {
        "NUMBER" => MapNumber(column),
        "VARCHAR2" or "NVARCHAR2" => MapVarChar(column),
        "CHAR" or "NCHAR" => MapChar(column),
        "CLOB" or "NCLOB" => "NVARCHAR(MAX)",
        "BLOB" => "VARBINARY(MAX)",
        "DATE" => "DATETIME2",
        "TIMESTAMP" or "TIMESTAMP WITH TIME ZONE" or "TIMESTAMP WITH LOCAL TIME ZONE" => "DATETIMEOFFSET",
        "FLOAT" => "FLOAT",
        "BINARY_FLOAT" => "REAL",
        "BINARY_DOUBLE" => "FLOAT",
        "RAW" => "VARBINARY(2000)",
        "LONG" => "NVARCHAR(MAX)",
        _ => "NVARCHAR(MAX)"
    };
}

static string MapNumber(OracleColumn column)
{
    if (column.DataPrecision is null)
    {
        return "DECIMAL(38, 10)";
    }

    int precision = column.DataPrecision.Value;
    int scale = column.DataScale ?? 0;

    if (scale == 0)
    {
        if (precision <= 9)
        {
            return "INT";
        }

        if (precision <= 18)
        {
            return "BIGINT";
        }
    }

    precision = Math.Clamp(precision, 1, 38);
    scale = Math.Clamp(scale, 0, precision);

    return $"DECIMAL({precision}, {scale})";
}

static string MapVarChar(OracleColumn column)
{
    int length = column.DataLength ?? 255;
    return length > 4000 ? "NVARCHAR(MAX)" : $"NVARCHAR({length})";
}

static string MapChar(OracleColumn column)
{
    int length = column.DataLength ?? 1;
    return length > 4000 ? "CHAR(4000)" : $"CHAR({length})";
}

static void TransferData(
    OracleConnection oracleConnection,
    SqlConnection sqlConnection,
    string schema,
    string tableName,
    List<OracleColumn> columns)
{
    string oracleQuery = $"SELECT {string.Join(", ", columns.Select(c => $"\"{c.Name}\""))} FROM \"{schema}\".\"{tableName}\"";

    using var oracleCommand = new OracleCommand(oracleQuery, oracleConnection);
    oracleCommand.InitialLOBFetchSize = -1;
    oracleCommand.InitialLONGFetchSize = -1;
    //oracleCommand.FetchSize = oracleCommand.RowSize * 1024;
    using var reader = oracleCommand.ExecuteReader(CommandBehavior.SequentialAccess);


    //using var reader = oracleCommand.ExecuteReader();

    var dataTable = new DataTable();
    foreach (var column in columns)
    {
        dataTable.Columns.Add(new DataColumn(column.Name, typeof(object)));
        //Type netType = GetNetTypeForOracleColumn(column);
        //dataTable.Columns.Add(new DataColumn(column.Name, netType));
    }

    //while (reader.Read())
    //{
    //    var row = dataTable.NewRow();
    //    for (int i = 0; i < columns.Count; i++)
    //    {
    //        row[i] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
    //    }
    //    dataTable.Rows.Add(row);
    //}

    //while (reader.Read())
    //{
    //    var row = dataTable.NewRow();
    //    for (int i = 0; i < columns.Count; i++)
    //    {
    //        if (reader.IsDBNull(i))
    //        {
    //            row[i] = DBNull.Value;
    //        }
    //        else
    //        {
    //            object value = reader.GetValue(i);
    //            Type valueType = value.GetType();
    //            // Log or inspect the type to see what Oracle is actually returning
    //            row[i] = ConvertOracleValueToNetType(value, columns[i]);
    //        }
    //    }
    //    dataTable.Rows.Add(row);
    //}

    using var bulkCopy = new SqlBulkCopy(sqlConnection)
    {

        DestinationTableName = $"[dbo].[{tableName}]",
        BulkCopyTimeout = 0, // 0 = no timeout, or set to a specific value like 300 seconds
        BatchSize = 10000,    // Process in batches for better performance
        NotifyAfter = 10000   // Optional: for progress reporting
    };

    bulkCopy.WriteToServer(reader);
    foreach (var column in columns)
    {
        bulkCopy.ColumnMappings.Add(column.Name, column.Name);
    }

    // Write directly from the reader - no DataTable needed!
    bulkCopy.WriteToServer(reader);

}

static Type GetNetTypeForOracleColumn(OracleColumn column)
{
    string oracleType = column.DataType.ToUpperInvariant();

    return oracleType switch
    {
        //*
        "NUMBER" => typeof(decimal),
        "VARCHAR2" or "NVARCHAR2" or "CHAR" or "NCHAR" => typeof(string),
        "CLOB" or "NCLOB" or "LONG" => typeof(string),
        "BLOB" or "RAW" => typeof(byte[]),
        "DATE" or "TIMESTAMP" or "TIMESTAMP WITH TIME ZONE" or "TIMESTAMP WITH LOCAL TIME ZONE" => typeof(DateTime),
        "FLOAT" or "BINARY_DOUBLE" => typeof(double),
        "BINARY_FLOAT" => typeof(float),
        _ => typeof(object)
        //*/

        /*
         "NUMBER" => MapNumber(column),
        "VARCHAR2" or "NVARCHAR2" => MapVarChar(column),
        "CHAR" or "NCHAR" => MapChar(column),
        "CLOB" or "NCLOB" => "NVARCHAR(MAX)",
        "BLOB" => "VARBINARY(MAX)",
        "DATE" => "DATETIME2",
        "TIMESTAMP" or "TIMESTAMP WITH TIME ZONE" or "TIMESTAMP WITH LOCAL TIME ZONE" => "DATETIMEOFFSET",
        "FLOAT" => "FLOAT",
        "BINARY_FLOAT" => "REAL",
        "BINARY_DOUBLE" => "FLOAT",
        "RAW" => "VARBINARY(2000)",
        "LONG" => "NVARCHAR(MAX)",
        _ => "NVARCHAR(MAX)"
         */

    };
}

static object ConvertOracleValueToNetType(object oracleValue, OracleColumn column)
{
    // Handle Oracle-specific types
    return oracleValue switch
    {
        Oracle.ManagedDataAccess.Types.OracleDecimal od => od.IsNull ? DBNull.Value : od.Value,
        Oracle.ManagedDataAccess.Types.OracleString os => os.IsNull ? DBNull.Value : os.Value,
        Oracle.ManagedDataAccess.Types.OracleTimeStamp ots => ots.IsNull ? DBNull.Value : ots.Value,
        Oracle.ManagedDataAccess.Types.OracleTimeStampTZ otstz => otstz.IsNull ? DBNull.Value : otstz.Value,
        Oracle.ManagedDataAccess.Types.OracleDate od => od.IsNull ? DBNull.Value : od.Value,
        Oracle.ManagedDataAccess.Types.OracleBlob ob => ob.IsNull ? DBNull.Value : ob.Value,
        Oracle.ManagedDataAccess.Types.OracleClob oc => oc.IsNull ? DBNull.Value : oc.Value,
        Oracle.ManagedDataAccess.Types.OracleBinary ob => ob.IsNull ? DBNull.Value : ob.Value,
        _ => oracleValue
    };
}


record OracleTable(string Schema, string Name);

record OracleColumn(
    string Name,
    string DataType,
    int? DataLength,
    int? DataPrecision,
    int? DataScale,
    bool Nullable);
