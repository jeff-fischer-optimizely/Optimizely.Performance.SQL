/*
    dbo.editDeletePageCheckInternal_optiperf_v1        OPT-0003        risk: Low

    Derived from EPiServer.Cms.Core 12.24.1.

    THE PROBLEM
    -----------
    This is the "what still points at the content I am about to delete" check, present in
    95 surveyed databases and accounting for six of the statements in the survey. It builds
    @Result, a table variable with no key and no index, from five INSERT..SELECTs, each of
    which filters on IN (SELECT ... FROM @pages) -- a second table variable, arriving as a
    table-valued parameter. It then deletes from @Result twice, once by scanning it against
    @pages again and once by joining it to tblContent.

    Below compatibility level 150 both table variables estimate at one row. The five
    inserts therefore build nested-loop plans over tblContentProperty, tblContentSoftlink
    and two self-joins of tblContentLanguage sized for a single page, when a bulk delete
    passes hundreds. The two DELETEs then scan an unindexed accumulator once per probe.

    WHAT CHANGED
    ------------
    Two things, both mechanical:

      1. OPTION (RECOMPILE) on the five INSERTs, the two DELETEs and the final SELECT, so
         each is compiled against the row counts actually in hand.

      2. @Result carries a nonclustered index on OwnerID. Both DELETEs filter on OwnerID
         and nothing else -- one against @pages, one against tblContent -- so the
         accumulator is probed on that column and never scanned.

    No predicate, join or projection was altered.

    WHY LOW RISK
    ------------
    Neither change can affect the result. A recompile hint is a compilation directive, and
    an index on a procedure-local table variable is invisible outside the procedure: it
    changes how rows are found, never which rows exist. @Result is declared, populated,
    read once and discarded within this body, so the index has no lifetime beyond the call
    and no interaction with anything the caller can observe.

    The ORDER BY ReferenceType on the final SELECT is untouched, so the output ordering is
    the original's. The index is not unique and imposes no constraint, so duplicate rows --
    which this procedure does produce, deliberately, one per referencing language -- are
    unaffected.

    PRECONDITIONS
    -------------
    maximumCompatibilityLevel 140, for the same reason as OPT-0002: at 150 and above the
    engine defers table variable compilation itself and the hints become pure compile cost.

    minimumSqlServerMajorVersion 12. Inline index definitions on a table variable are SQL
    Server 2014 syntax. Every surveyed database is well past that, but CMS 11 is supported
    on SQL Server 2012 and the gate makes the failure a skipped rewrite rather than a
    deployment error.
*/

IF OBJECT_ID('dbo.editDeletePageCheckInternal_optiperf_v1', 'P') IS NOT NULL
    DROP PROCEDURE dbo.editDeletePageCheckInternal_optiperf_v1;
GO

CREATE PROCEDURE dbo.editDeletePageCheckInternal_optiperf_v1
(@pages editDeletePageInternalTable READONLY)
AS
BEGIN
	SET NOCOUNT ON

	DECLARE @Result AS TABLE	(
		OwnerLanguageID INT NULL,
		ReferencedLanguageID INT,
		OwnerID INT NOT NULL,
		OwnerName NVARCHAR(255),
		ReferencedID INT,
		ReferencedName NVARCHAR(255),
		ReferenceType INT NOT NULL,
		/* OPTIPERF: both DELETEs below probe this accumulator on OwnerID alone. */
		INDEX IX_Result_OwnerID NONCLUSTERED (OwnerID)
	)

	INSERT INTO @Result
	SELECT
		tblContentLanguage.fkLanguageBranchID AS OwnerLanguageID,
		NULL AS ReferencedLanguageID,
		tblContentLanguage.fkContentID AS OwnerID,
		tblContentLanguage.Name As OwnerName,
		ContentLink As ReferencedID,
		tpl.Name AS ReferencedName,
		0 AS ReferenceType
	FROM
		tblContentProperty
	INNER JOIN
		tblContent ON tblContentProperty.fkContentID=tblContent.pkID
	INNER JOIN
		tblContentLanguage ON tblContentLanguage.fkContentID=tblContent.pkID
	INNER JOIN
		tblContent AS tp ON ContentLink=tp.pkID
	INNER JOIN
		tblContentLanguage AS tpl ON tpl.fkContentID=tp.pkID
	WHERE
		(ContentLink IN (SELECT pkID FROM @pages)) AND
		tblContentLanguage.fkLanguageBranchID=tblContentProperty.fkLanguageBranchID AND
		tpl.fkLanguageBranchID=tp.fkMasterLanguageBranchID
	OPTION (RECOMPILE)	/* OPTIPERF */

	INSERT INTO @Result
	SELECT
		tblContentLanguage.fkLanguageBranchID AS OwnerLanguageID,
		NULL AS ReferencedLanguageID,
		tblContentLanguage.fkContentID AS OwnerID,
		tblContentLanguage.Name As OwnerName,
		tp.pkID AS ReferencedID,
		tpl.Name AS ReferencedName,
		1 AS ReferenceType
	FROM
		tblContentLanguage
	INNER JOIN
		tblContent ON tblContent.pkID=tblContentLanguage.fkContentID
	INNER JOIN
		tblContent AS tp ON tblContentLanguage.ContentLinkGUID = tp.ContentGUID
	INNER JOIN
		tblContentLanguage AS tpl ON tpl.fkContentID=tp.pkID
	WHERE
		(tblContentLanguage.ContentLinkGUID IS NOT NULL AND tblContentLanguage.ContentLinkGUID IN (SELECT PageGUID FROM @pages)) AND
		tpl.fkLanguageBranchID=tp.fkMasterLanguageBranchID
	OPTION (RECOMPILE)	/* OPTIPERF */

	INSERT INTO @Result
	SELECT
		tblContentSoftlink.OwnerLanguageID AS OwnerLanguageID,
		tblContentSoftlink.ReferencedLanguageID AS ReferencedLanguageID,
		PLinkFrom.pkID AS OwnerID,
		PLinkFromLang.Name  As OwnerName,
		PLinkTo.pkID AS ReferencedID,
		PLinkToLang.Name AS ReferencedName,
		1 AS ReferenceType
	FROM
		tblContentSoftlink
	INNER JOIN
		tblContent AS PLinkFrom ON PLinkFrom.pkID=tblContentSoftlink.fkOwnerContentID
	INNER JOIN
		tblContentLanguage AS PLinkFromLang ON PLinkFromLang.fkContentID=PLinkFrom.pkID
	INNER JOIN
		tblContent AS PLinkTo ON PLinkTo.ContentGUID=tblContentSoftlink.fkReferencedContentGUID
	INNER JOIN
		tblContentLanguage AS PLinkToLang ON PLinkToLang.fkContentID=PLinkTo.pkID
	WHERE
		(PLinkTo.pkID IN (SELECT pkID FROM @pages)) AND
		PLinkFromLang.fkLanguageBranchID=PLinkFrom.fkMasterLanguageBranchID AND
		PLinkToLang.fkLanguageBranchID=PLinkTo.fkMasterLanguageBranchID
	OPTION (RECOMPILE)	/* OPTIPERF */

	INSERT INTO @Result
	SELECT
		tblContentLanguage.fkLanguageBranchID AS OwnerLanguageID,
		NULL AS ReferencedLanguageID,
		tblContent.pkID AS OwnerID,
		tblContentLanguage.Name  As OwnerName,
		tp.pkID AS ReferencedID,
		tpl.Name AS ReferencedName,
		2 AS ReferenceType
	FROM
		tblContent
	INNER JOIN
		tblContentLanguage ON tblContentLanguage.fkContentID=tblContent.pkID
	INNER JOIN
		tblContent AS tp ON tblContent.ArchiveContentGUID IS NOT NULL AND tblContent.ArchiveContentGUID=tp.ContentGUID
	INNER JOIN
		tblContentLanguage AS tpl ON tpl.fkContentID=tp.pkID
	WHERE
		(tblContent.ArchiveContentGUID IS NOT NULL AND tblContent.ArchiveContentGUID IN (SELECT PageGUID FROM @pages)) AND
		tpl.fkLanguageBranchID=tp.fkMasterLanguageBranchID AND
		tblContentLanguage.fkLanguageBranchID=tblContent.fkMasterLanguageBranchID
	OPTION (RECOMPILE)	/* OPTIPERF */

	DELETE
		result
	FROM
		@Result result
	WHERE
		result.OwnerID IN (SELECT pkID FROM @pages)
	OPTION (RECOMPILE)	/* OPTIPERF */

	DELETE
		result
	FROM
		@Result result
	INNER JOIN
		tblContent ON result.OwnerID = tblContent.pkID
	WHERE
		tblContent.Deleted != 0
	OPTION (RECOMPILE)	/* OPTIPERF */

	INSERT INTO @Result
	SELECT
		tblContentLanguage.fkLanguageBranchID AS OwnerLanguageID,
		NULL AS ReferencedLanguageID,
		tblContent.pkID AS OwnerID,
		tblContentLanguage.Name  As OwnerName,
		tblContentTypeDefault.fkArchiveContentID AS ReferencedID,
		tblContentType.Name AS ReferencedName,
		3 AS ReferenceType
	FROM
		tblContentTypeDefault
	INNER JOIN
	   tblContentType ON tblContentTypeDefault.fkContentTypeID=tblContentType.pkID
	INNER JOIN
		tblContent ON tblContentTypeDefault.fkArchiveContentID=tblContent.pkID
	INNER JOIN
		tblContentLanguage ON tblContentLanguage.fkContentID=tblContent.pkID
	WHERE
		tblContentTypeDefault.fkArchiveContentID IN (SELECT pkID FROM @pages) AND
		tblContentLanguage.fkLanguageBranchID=tblContent.fkMasterLanguageBranchID
	OPTION (RECOMPILE)	/* OPTIPERF */

	SELECT
		OwnerLanguageID,
		ReferencedLanguageID ,
		OwnerID,
		OwnerName,
		ReferencedID,
		ReferencedName,
		ReferenceType
	FROM
		@Result result
	ORDER BY
	   ReferenceType
	OPTION (RECOMPILE)	/* OPTIPERF */

	RETURN 0
END
GO
