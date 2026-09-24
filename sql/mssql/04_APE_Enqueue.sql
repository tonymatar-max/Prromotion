-- Nexus Promotions (APE) — MS SQL, script 4 of 5: Mode B queueing, called from SBO_SP_PostTransactionNotice.
--
-- Queues a sales quotation or order for the worker when it was saved without a promotion result
-- (U_APE_Hash empty): created by the Service Layer, DI API, B1 Integrator, e-commerce, or an update that
-- cleared the hash. Mode A documents already carry a hash and are skipped (FR-39).
-- Loop guard (FR-41): the worker's own PATCH writes a hash, and its user is skipped as well. A document the worker
-- gave up on (U_APE_Status Failed or Skipped) is not queued again by its own status update; to retry it, clear
-- U_APE_Status (or re-apply promotions in the add-on).

CREATE OR ALTER PROCEDURE dbo.APE_Enqueue
    @ObjType   NVARCHAR(20),
    @TransType NCHAR(1),
    @DocKey    NVARCHAR(255)
AS
BEGIN
    SET NOCOUNT ON;

    IF @TransType NOT IN (N'A', N'U') OR @ObjType NOT IN (N'23', N'17') RETURN;
    IF ISNULL((SELECT Value FROM dbo.APE_Settings WHERE Name = N'ModeB'), N'N') <> N'Y' RETURN;

    DECLARE @DocEntry INT = CASE WHEN LEN(@DocKey) BETWEEN 1 AND 9 AND @DocKey NOT LIKE N'%[^0-9]%' THEN CAST(@DocKey AS INT) END;
    DECLARE @HeaderTable SYSNAME = (SELECT HeaderTable FROM dbo.APE_DocTables(@ObjType));
    IF @DocEntry IS NULL OR @HeaderTable IS NULL RETURN;

    DECLARE @Hash NVARCHAR(64), @DocStatus NCHAR(1), @UserSign INT, @ApeStatus NVARCHAR(10);
    DECLARE @Sql NVARCHAR(MAX) = N'
        SELECT @Hash = U_APE_Hash, @DocStatus = DocStatus, @ApeStatus = U_APE_Status,
               @UserSign = CASE WHEN @TransType = N''A'' THEN UserSign ELSE ISNULL(UserSign2, UserSign) END
        FROM dbo.' + QUOTENAME(@HeaderTable) + N' WHERE DocEntry = @DocEntry;';
    EXEC sys.sp_executesql @Sql,
        N'@DocEntry INT, @TransType NCHAR(1), @Hash NVARCHAR(64) OUTPUT, @DocStatus NCHAR(1) OUTPUT, @UserSign INT OUTPUT, @ApeStatus NVARCHAR(10) OUTPUT',
        @DocEntry = @DocEntry, @TransType = @TransType, @Hash = @Hash OUTPUT, @DocStatus = @DocStatus OUTPUT,
        @UserSign = @UserSign OUTPUT, @ApeStatus = @ApeStatus OUTPUT;

    IF @DocStatus IS NULL OR @DocStatus = N'C' RETURN;   -- missing or closed
    IF @Hash IS NOT NULL AND @Hash <> N'' RETURN;          -- already evaluated (Mode A or worker)
    IF @ApeStatus IN (N'Failed', N'Skipped') RETURN;        -- the worker gave up on it: no retry loop
    DECLARE @TechUser NVARCHAR(200) = (SELECT Value FROM dbo.APE_Settings WHERE Name = N'TechUserId');
    IF @UserSign = CASE WHEN LEN(@TechUser) BETWEEN 1 AND 9 AND @TechUser NOT LIKE N'%[^0-9]%' THEN CAST(@TechUser AS INT) END RETURN;

    IF EXISTS (SELECT 1 FROM dbo.APE_Queue WITH (UPDLOCK, HOLDLOCK)
               WHERE ObjType = @ObjType AND DocEntry = @DocEntry AND Status IN (N'New', N'Processing'))
        RETURN;

    INSERT dbo.APE_Queue (ObjType, DocEntry, TransType) VALUES (@ObjType, @DocEntry, @TransType);
END
GO
