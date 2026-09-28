-- One-time server setup for CmdManager on SQL Server 2025 Express (local default instance, Windows auth).
-- Run as a sysadmin, e.g.:  sqlcmd -S . -E -i db\server-setup.sql
-- The IIS app pool "CmdManager" must exist first (its virtual account IIS APPPOOL\CmdManager is used below).
-- Afterwards the API creates/updates the schema itself (Database:MigrateOnStartup=true); alternatively run
-- db\CmdManager-schema.sql (idempotent) with:  sqlcmd -S . -E -d CmdManager -i db\CmdManager-schema.sql

IF DB_ID(N'CmdManager') IS NULL
    CREATE DATABASE [CmdManager];
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'IIS APPPOOL\CmdManager')
    CREATE LOGIN [IIS APPPOOL\CmdManager] FROM WINDOWS WITH DEFAULT_DATABASE = [CmdManager];
GO

USE [CmdManager];
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'IIS APPPOOL\CmdManager')
    CREATE USER [IIS APPPOOL\CmdManager] FOR LOGIN [IIS APPPOOL\CmdManager];
GO

ALTER ROLE db_datareader ADD MEMBER [IIS APPPOOL\CmdManager];
ALTER ROLE db_datawriter ADD MEMBER [IIS APPPOOL\CmdManager];
ALTER ROLE db_ddladmin   ADD MEMBER [IIS APPPOOL\CmdManager];  -- needed only for MigrateOnStartup
GO
