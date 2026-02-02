# Quick Start Guide

## Running the Migration Tool

### 1. Set up environment variables

**Linux/macOS:**
```bash
export ORACLE_CONNECTION_STRING="User Id=your_user;Password=your_password;Data Source=localhost:1521/ORCL"
export MSSQL_CONNECTION_STRING="Server=localhost;Database=TargetDB;User Id=sa;Password=YourPassword;TrustServerCertificate=True"
```

**Windows PowerShell:**
```powershell
$env:ORACLE_CONNECTION_STRING="User Id=your_user;Password=your_password;Data Source=localhost:1521/ORCL"
$env:MSSQL_CONNECTION_STRING="Server=localhost;Database=TargetDB;User Id=sa;Password=YourPassword;TrustServerCertificate=True"
```

### 2. Run the tool

```bash
dotnet run
```

### 3. Expected Output

```
Oracle to MSSQL Database Migration Tool
========================================

Starting migration process...

Found 5 tables in Oracle database.

Processing table: CUSTOMERS
  Created table structure
  Copied 1000 rows
✓ Successfully migrated table: CUSTOMERS

Processing table: ORDERS
  Created table structure
  Copied 5000 rows
✓ Successfully migrated table: ORDERS

...

Migration completed!
```

## What Gets Migrated

✅ **Included:**
- All table structures (columns, data types)
- Primary key constraints
- All table data
- NULL/NOT NULL constraints

❌ **Excluded (by design):**
- Foreign key relationships
- Indexes (other than primary keys)
- Triggers
- Stored procedures
- Views

## Notes

- If tables already exist in MSSQL, they will be dropped and recreated
- Data is transferred in batches of 1000 rows for optimal performance
- Each batch is wrapped in a transaction for data integrity
- If any table migration fails, the tool continues with the next table
