IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260928011657_InitialCreate'
)
BEGIN
    CREATE TABLE [Users] (
        [Id] int NOT NULL IDENTITY,
        [UserName] nvarchar(64) NOT NULL,
        [NormalizedUserName] nvarchar(64) NOT NULL,
        [Email] nvarchar(256) NULL,
        [PasswordHash] nvarchar(512) NOT NULL,
        [CreatedUtc] datetime2 NOT NULL,
        [LastLoginUtc] datetime2 NULL,
        [AccessFailedCount] int NOT NULL,
        [LockoutEndUtc] datetime2 NULL,
        [IsDisabled] bit NOT NULL,
        CONSTRAINT [PK_Users] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260928011657_InitialCreate'
)
BEGIN
    CREATE TABLE [Assets] (
        [Id] int NOT NULL IDENTITY,
        [Content] varbinary(max) NOT NULL,
        [UserId] int NOT NULL,
        [Name] nvarchar(255) NOT NULL,
        [Folder] nvarchar(400) NOT NULL,
        [RelativePath] nvarchar(400) NOT NULL,
        [PathKey] nvarchar(400) NOT NULL,
        [Description] nvarchar(1000) NULL,
        [Size] bigint NOT NULL,
        [Sha256] char(64) NOT NULL,
        [CreatedUtc] datetime2 NOT NULL,
        [UpdatedUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_Assets] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Assets_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260928011657_InitialCreate'
)
BEGIN
    CREATE TABLE [Commands] (
        [Id] int NOT NULL IDENTITY,
        [Kind] varchar(16) NOT NULL,
        [Tags] nvarchar(1000) NULL,
        [IsBinary] bit NOT NULL,
        [TextContent] nvarchar(max) NULL,
        [BinaryContent] varbinary(max) NULL,
        [UserId] int NOT NULL,
        [Name] nvarchar(255) NOT NULL,
        [Folder] nvarchar(400) NOT NULL,
        [RelativePath] nvarchar(400) NOT NULL,
        [PathKey] nvarchar(400) NOT NULL,
        [Description] nvarchar(1000) NULL,
        [Size] bigint NOT NULL,
        [Sha256] char(64) NOT NULL,
        [CreatedUtc] datetime2 NOT NULL,
        [UpdatedUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_Commands] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Commands_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260928011657_InitialCreate'
)
BEGIN
    CREATE TABLE [RefreshTokens] (
        [Id] int NOT NULL IDENTITY,
        [UserId] int NOT NULL,
        [TokenHash] varchar(64) NOT NULL,
        [CreatedUtc] datetime2 NOT NULL,
        [ExpiresUtc] datetime2 NOT NULL,
        [RevokedUtc] datetime2 NULL,
        CONSTRAINT [PK_RefreshTokens] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_RefreshTokens_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260928011657_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Assets_UserId_PathKey] ON [Assets] ([UserId], [PathKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260928011657_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Commands_UserId_Kind] ON [Commands] ([UserId], [Kind]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260928011657_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Commands_UserId_PathKey] ON [Commands] ([UserId], [PathKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260928011657_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_RefreshTokens_TokenHash] ON [RefreshTokens] ([TokenHash]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260928011657_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_RefreshTokens_UserId] ON [RefreshTokens] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260928011657_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Users_NormalizedUserName] ON [Users] ([NormalizedUserName]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260928011657_InitialCreate'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260928011657_InitialCreate', N'10.0.12');
END;

COMMIT;
GO

