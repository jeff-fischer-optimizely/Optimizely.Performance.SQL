/*
    dbo.editDeletePageCheckInternal_cms11_optiperf_v1        OPT-0013        risk: Low

    Derived from EPiServer.Cms.Core 11.21.5. The CMS 11 counterpart to OPT-0003.

    Same rewrite, same reasoning. The procedure builds @Result from five INSERTs -- one per
    kind of reference that would be broken by a delete -- then prunes it with two DELETEs and
    returns it ordered. @Result is a table variable, so below compatibility level 150 every
    statement after the first INSERT is planned as though it held one row, and the two
    DELETEs and the final sort are planned against an unindexed heap.

    A nonclustered index on OwnerID, and OPTION (RECOMPILE) on all eight statements. No join,
    predicate or projection is altered.

    The CMS 11 body differs from CMS 12 by eleven lines, all of them IS NOT NULL guards CMS 12
    added in two of the archive-reference INSERTs. Those lines are reproduced here exactly as
    CMS 11 ships them -- the guards are a CMS 12 correctness change and adding them here would
    be a behaviour change wearing a performance rewrite's clothes.

    Low risk: an index on a table variable and a recompile directive cannot change which rows
    a statement returns.

    Preconditions: maximumCompatibilityLevel 140, minimumSqlServerMajorVersion 12.
*/

IF OBJECT_ID('dbo.editDeletePageCheckInternal_cms11_optiperf_v1', 'P') IS NOT NULL
    DROP PROCEDURE dbo.editDeletePageCheckInternal_cms11_optiperf_v1;
GO

CREATE PROCEDURE [dbo].[editDeletePageCheckInternal_cms11_optiperf_v1]
(@pages editDeletePageInternalTable READONLY) 
AS
BEGIN
	SET NOCOUNT ON

	/* OPTIPERF: both DELETEs and the final ORDER BY drive off OwnerID, and a table variable
	   has no index at all without this. Requires SQL Server 2014 or later, which the
	   precondition enforces. */
	DECLARE @Result AS TABLE	(
		OwnerLanguageID INT NULL,
		ReferencedLanguageID INT,
		OwnerID INT NOT NULL, 
		OwnerName NVARCHAR(255),
		ReferencedID INT,
		ReferencedName NVARCHAR(255),
		ReferenceType INT NOT NULL,
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
		(tblContentLanguage.ContentLinkGUID IN (SELECT PageGUID FROM @pages)) AND
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
		tblContent AS tp ON tblContent.ArchiveContentGUID=tp.ContentGUID
	INNER JOIN
		tblContentLanguage AS tpl ON tpl.fkContentID=tp.pkID
	WHERE
		(tblContent.ArchiveContentGUID IN (SELECT PageGUID FROM @pages)) AND
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
