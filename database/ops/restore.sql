/* Point-in-time restore of LinguaPath (docs/OPERATIONS.md, "Restore drill").
   1. Stop the API and admin (docker compose stop api admin) so nothing writes during the restore.
   2. Restore the last full backup WITH NORECOVERY, then every log backup up to the moment you want, then RECOVERY.
   Restoring to a new name first (LinguaPath_Restore) lets you check the data before swapping. */

DECLARE @target sysname = N'$(Target)';                -- e.g. LinguaPath_Restore
DECLARE @full nvarchar(400) = N'$(FullBackup)';        -- e.g. /var/opt/mssql/backup/LinguaPath-full-20261117-0200.bak
DECLARE @stopAt datetime2 = TRY_CONVERT(datetime2, N'$(StopAt)');  -- UTC, e.g. 2026-11-17T14:30:00 (empty = latest)

-- Logical file names from the backup (the data and log files get new names under the target database).
DECLARE @files TABLE (LogicalName nvarchar(128), PhysicalName nvarchar(260), Type char(1), FileGroupName nvarchar(128) NULL,
    Size numeric(20,0), MaxSize numeric(20,0), FileId bigint, CreateLSN numeric(25,0), DropLSN numeric(25,0) NULL,
    UniqueId uniqueidentifier, ReadOnlyLSN numeric(25,0) NULL, ReadWriteLSN numeric(25,0) NULL, BackupSizeInBytes bigint,
    SourceBlockSize int, FileGroupId int, LogGroupGUID uniqueidentifier NULL, DifferentialBaseLSN numeric(25,0) NULL,
    DifferentialBaseGUID uniqueidentifier NULL, IsReadOnly bit, IsPresent bit, TDEThumbprint varbinary(32) NULL, SnapshotUrl nvarchar(360) NULL);
INSERT @files EXEC (N'RESTORE FILELISTONLY FROM DISK = N''' + @full + N'''');

DECLARE @data sysname = (SELECT LogicalName FROM @files WHERE Type = 'D');
DECLARE @logf sysname = (SELECT LogicalName FROM @files WHERE Type = 'L');
DECLARE @dir nvarchar(260) = CONVERT(nvarchar(260), SERVERPROPERTY('InstanceDefaultDataPath'));

DECLARE @sql nvarchar(max) = N'RESTORE DATABASE ' + QUOTENAME(@target) + N' FROM DISK = N''' + @full + N''' WITH NORECOVERY, REPLACE, '
    + N'MOVE N''' + @data + N''' TO N''' + @dir + @target + N'.mdf'', MOVE N''' + @logf + N''' TO N''' + @dir + @target + N'_log.ldf'', STATS = 10;';
EXEC (@sql);

/* Then, for each log backup after the full one, oldest first:
     RESTORE LOG [LinguaPath_Restore] FROM DISK = N'.../LinguaPath-log-20261117-0300.trn' WITH NORECOVERY;
   and for the last one, stop at the moment before the incident:
     RESTORE LOG [LinguaPath_Restore] FROM DISK = N'.../LinguaPath-log-20261117-1500.trn' WITH STOPAT = '2026-11-17T14:30:00', NORECOVERY;
   Finally: */
IF @stopAt IS NULL PRINT N'Now restore the log backups (see above), then run: RESTORE DATABASE ' + QUOTENAME(@target) + N' WITH RECOVERY;';

/* Check, then swap names:
     ALTER DATABASE [LinguaPath] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
     ALTER DATABASE [LinguaPath] MODIFY NAME = [LinguaPath_Old];
     ALTER DATABASE [LinguaPath_Restore] MODIFY NAME = [LinguaPath];
     ALTER DATABASE [LinguaPath] SET MULTI_USER;
   Uploaded files (photos, recordings, certificates) are in the uploads volume: restore that from its own copy of the same night. */
