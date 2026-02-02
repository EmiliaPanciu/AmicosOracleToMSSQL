# AmicosOracleToMSSQL

A .NET console application to copy database structure and data from Oracle to Microsoft SQL Server (MSSQL) without foreign key relationships.

## Features

- ✅ Automatically discovers all tables in Oracle database
- ✅ Creates corresponding tables in MSSQL database
- ✅ Migrates table schemas with proper data type conversion
- ✅ Preserves primary key constraints
- ✅ **Excludes foreign key relationships** (as per design)
- ✅ Copies all data from Oracle to MSSQL
- ✅ Batch processing for efficient data transfer
- ✅ Comprehensive error handling and logging

## Prerequisites

- .NET 10.0 or later
- Access to Oracle database (source)
- Access to Microsoft SQL Server database (target)
- Appropriate connection permissions for both databases

## Installation

1. Clone the repository:
```bash
git clone https://github.com/EmiliaPanciu/AmicosOracleToMSSQL.git
cd AmicosOracleToMSSQL
```

2. Restore NuGet packages:
```bash
dotnet restore
```

3. Build the project:
```bash
dotnet build
```

## Usage

### Option 1: Using Environment Variables (Recommended)

Set up your connection strings as environment variables:

**Linux/macOS:**
```bash
export ORACLE_CONNECTION_STRING="User Id=your_user;Password=your_password;Data Source=hostname:port/servicename"
export MSSQL_CONNECTION_STRING="Server=hostname;Database=database;User Id=username;Password=password;TrustServerCertificate=True"
dotnet run
```

**Windows (PowerShell):**
```powershell
$env:ORACLE_CONNECTION_STRING="User Id=your_user;Password=your_password;Data Source=hostname:port/servicename"
$env:MSSQL_CONNECTION_STRING="Server=hostname;Database=database;User Id=username;Password=password;TrustServerCertificate=True"
dotnet run
```

**Windows (Command Prompt):**
```cmd
set ORACLE_CONNECTION_STRING=User Id=your_user;Password=your_password;Data Source=hostname:port/servicename
set MSSQL_CONNECTION_STRING=Server=hostname;Database=database;User Id=username;Password=password;TrustServerCertificate=True
dotnet run
```

### Option 2: Interactive Mode

Run the application and provide connection strings when prompted:
```bash
dotnet run
```

## Connection String Formats

### Oracle Connection String
```
User Id=username;Password=password;Data Source=hostname:port/servicename
```

Example:
```
User Id=scott;Password=tiger;Data Source=localhost:1521/ORCL
```

### MSSQL Connection String
```
Server=hostname;Database=database_name;User Id=username;Password=password;TrustServerCertificate=True
```

Example:
```
Server=localhost;Database=TargetDB;User Id=sa;Password=MyPassword123;TrustServerCertificate=True
```

## How It Works

1. **Discovery**: Connects to Oracle database and retrieves list of all user tables
2. **Schema Extraction**: For each table, extracts:
   - Column names
   - Data types
   - Lengths/precision/scale
   - Nullable constraints
   - Primary key constraints
   - **Note: Foreign key constraints are intentionally excluded**
3. **Table Creation**: Creates equivalent tables in MSSQL with:
   - Converted data types (Oracle → MSSQL mapping)
   - Primary key constraints
   - **No foreign key relationships**
4. **Data Migration**: Copies all data from Oracle to MSSQL in batches (1000 rows per batch)

## Data Type Mapping

| Oracle Type | MSSQL Type |
|------------|------------|
| VARCHAR2 | NVARCHAR |
| NUMBER | INT/BIGINT/DECIMAL |
| DATE | DATETIME2 |
| TIMESTAMP | DATETIME2 |
| CLOB | NVARCHAR(MAX) |
| BLOB | VARBINARY(MAX) |

## Important Notes

- **Foreign Keys**: This tool intentionally does **NOT** migrate foreign key relationships between tables. Only table structures and data are copied.
- **Existing Tables**: If a table already exists in the target MSSQL database, it will be **dropped and recreated**.
- **Primary Keys**: Primary key constraints are preserved during migration.
- **Batch Processing**: Data is inserted in batches of 1000 rows for optimal performance.
- **Transactions**: Each batch is processed in a transaction to ensure data integrity.

## Troubleshooting

### Connection Issues

If you encounter connection problems:

1. **Oracle**: Ensure the Oracle client is properly installed and configured
2. **MSSQL**: Verify that TCP/IP connections are enabled on the SQL Server
3. **Firewall**: Check that firewall rules allow connections to both databases
4. **Credentials**: Verify that user credentials have appropriate permissions

### Required Permissions

**Oracle (Source):**
- SELECT on user_tables
- SELECT on user_tab_columns
- SELECT on user_constraints
- SELECT on user_cons_columns
- SELECT on all data tables

**MSSQL (Target):**
- CREATE TABLE
- DROP TABLE
- INSERT data

## License

This project is open source. Please check the repository for license details.

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.
