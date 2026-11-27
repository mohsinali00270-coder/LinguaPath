/* Quick health check for the on-call runbook (docs/OPERATIONS.md). Read-only. */
SELECT name, state_desc, recovery_model_desc, log_reuse_wait_desc FROM sys.databases WHERE name = N'LinguaPath';

-- Last backups
SELECT TOP (5) type, backup_finish_date, CAST(compressed_backup_size / 1048576.0 AS decimal(10,1)) AS size_mb
FROM msdb.dbo.backupset WHERE database_name = N'LinguaPath' ORDER BY backup_finish_date DESC;

-- Biggest tables
SELECT TOP (10) t.name, SUM(p.rows) AS rows_count
FROM sys.tables t JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
GROUP BY t.name ORDER BY rows_count DESC;

-- Work queues that should stay small
USE [LinguaPath];
SELECT (SELECT COUNT(*) FROM AiGenerationRequests WHERE Status IN (1, 2)) AS ai_jobs_queued_or_running,
       (SELECT COUNT(*) FROM Notifications WHERE ReadAt IS NULL) AS unread_notifications,
       (SELECT COUNT(*) FROM PracticeAttempts WHERE AudioUrl IS NOT NULL AND CreatedAt < DATEADD(day, -31, SYSUTCDATETIME())) AS recordings_past_retention;
