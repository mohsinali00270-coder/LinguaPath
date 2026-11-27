/* LinguaPath SQL Server backups (docs/OPERATIONS.md).
   Recovery model FULL + nightly full + hourly log backups gives point-in-time restore with at most 1 hour of loss (NFR-07).
   Run with sqlcmd or a SQL Agent job; in Docker the path below is the ./deploy/backups volume. */

ALTER DATABASE [LinguaPath] SET RECOVERY FULL;
GO

DECLARE @stamp nvarchar(20) = FORMAT(SYSUTCDATETIME(), 'yyyyMMdd-HHmm');
DECLARE @folder nvarchar(200) = N'/var/opt/mssql/backup/';   -- Windows: N'D:\Backups\LinguaPath\'
DECLARE @kind nvarchar(10) = N'$(Kind)';                     -- sqlcmd -v Kind=FULL | LOG | DIFF

IF @kind = N'FULL'
BEGIN
    DECLARE @full nvarchar(400) = @folder + N'LinguaPath-full-' + @stamp + N'.bak';
    BACKUP DATABASE [LinguaPath] TO DISK = @full WITH COMPRESSION, CHECKSUM, INIT, STATS = 10;
    RESTORE VERIFYONLY FROM DISK = @full WITH CHECKSUM;
END
ELSE IF @kind = N'DIFF'
BEGIN
    DECLARE @diff nvarchar(400) = @folder + N'LinguaPath-diff-' + @stamp + N'.bak';
    BACKUP DATABASE [LinguaPath] TO DISK = @diff WITH DIFFERENTIAL, COMPRESSION, CHECKSUM, INIT;
END
ELSE
BEGIN
    DECLARE @log nvarchar(400) = @folder + N'LinguaPath-log-' + @stamp + N'.trn';
    BACKUP LOG [LinguaPath] TO DISK = @log WITH COMPRESSION, CHECKSUM, INIT;
END
GO

/* Examples
   sqlcmd -S localhost -U sa -P "<password>" -C -v Kind=FULL -i database/ops/backup.sql
   docker compose -f deploy/docker-compose.yml exec sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SQL_SA_PASSWORD" -C -v Kind=LOG -i /var/opt/mssql/backup/backup.sql
   Keep 35 days of files, copy them off the server every night (Azure Blob, S3, another site), and test a restore monthly. */
