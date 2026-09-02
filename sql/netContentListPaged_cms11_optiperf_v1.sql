/*
    dbo.netContentListPaged_cms11_optiperf_v1        OPT-0010        risk: Low

    Derived from EPiServer.Cms.Core 11.21.5. CMS 11 only.

    This is the unpack fix and nothing else. It is correct at every compatibility level, so
    it carries no ceiling -- see OPT-0011 for the same body plus the table variable recompile
    hints, which is the one to use below level 150. The two have disjoint compatibility
    windows: this entry requires 150 or above, OPT-0011 caps at 140. Exactly one is eligible
    on any given database, decided by the capability probe rather than by configuration.

    At 150 and above the engine defers table variable compilation on its own, so the hints
    OPT-0011 adds would be pure compile cost on seven statements per call. What remains worth
    fixing there is the loop, and the loop is worth fixing everywhere.

    THE FIX THAT MATTERS: THE UNPACK LOOP
    -------------------------------------
    CMS 11 has no table-valued parameter here. The caller packs content ids into a
    VARBINARY(8000) and the procedure unpacks them:

        SET @Index = 1
        SET @Length = DATALENGTH(@Binary)
        WHILE (@Index <= @Length)
        BEGIN
            INSERT INTO @ContentItems(LocalPageID) VALUES(SUBSTRING(@Binary, @Index, 4))
            SET @Index = @Index + 4
        END

    One statement execution per content id. The fleet survey records that INSERT on its own,
    in 99 databases, at 7.6 billion executions -- the highest execution count of any statement
    in the survey by a wide margin. Individually each is trivial; collectively it is a loop
    doing four bytes of work per statement round trip, up to 2,000 times per call, on the
    procedure that loads every content listing in the product.

    It is replaced by a tally CTE and one set-based INSERT. The offsets generated are
    1, 5, 9, ... -- exactly those the loop visited -- and the row count is
    ceil(@Length / 4), which is exactly the loop's termination test. A trailing partial
    group, if @Binary were ever not a multiple of four bytes, is still produced, and
    SUBSTRING returns the same short value the loop would have inserted.

    Insert order into the table variable is not preserved, and does not need to be: every
    statement that reads @ContentItems has an explicit ORDER BY, and none of them order by
    anything that came from insertion sequence. The result sets are byte-identical.

    That argument was also tested rather than only made. Both forms were run side by side
    against 200 randomly generated payloads of 0 to 300 ids, with roughly one in seven
    deliberately truncated to a non-multiple of four bytes to exercise the partial tail. The
    multisets produced were identical in every trial.

    THE CMS 11 PROCEDURE IS NOT THE CMS 12 PROCEDURE
    ------------------------------------------------
    Worth stating plainly, because the names match and the bodies do not. CMS 12 takes an
    IDTable parameter and a ContentLanguageTable; CMS 11 takes @Binary and builds
    @ContentItems with LocalPageID / LocalLanguageID. Roughly 78 lines differ. This is a
    separately derived body, not a port, and it carries its own originalBodyHash. A CMS 12
    database can never match it, and vice versa -- the hash gate makes that structural
    rather than a matter of getting appliesTo right.

    WHY LOW RISK
    ------------
    No join, predicate, projection or ORDER BY is touched. The only change is how
    @ContentItems is populated, and the argument that it is populated with the same rows is
    an arithmetic one that holds for every input, given above. Nothing here depends on the
    optimiser choosing well.

    Preconditions: minimumCompatibilityLevel 150.
*/

IF OBJECT_ID('dbo.netContentListPaged_cms11_optiperf_v1', 'P') IS NOT NULL
    DROP PROCEDURE dbo.netContentListPaged_cms11_optiperf_v1;
GO

CREATE PROCEDURE [dbo].[netContentListPaged_cms11_optiperf_v1]
(
	@Binary VARBINARY(8000),
	@Threshold INT = 0,
	@LanguageBranchID INT
)
AS
BEGIN
	SET NOCOUNT ON

	DECLARE @ContentItems TABLE (LocalPageID INT, LocalLanguageID INT)
	DECLARE	@Length SMALLINT
	SET @Length = DATALENGTH(@Binary)

	/* OPTIPERF: the original walked @Binary four bytes at a time in a WHILE loop, issuing one
	   single-row INSERT per content id. That loop is the highest-execution statement in the
	   entire fleet survey -- 7.6 billion executions -- and every iteration is a statement
	   compile, a table variable insert and a loop test for four bytes of payload.

	   The tally CTE produces the same offsets the loop visited (1, 5, 9, ...) and the same
	   SUBSTRING at each, in one set-based INSERT. Iteration count is ceil(@Length / 4), which
	   matches the loop's termination test @Index <= @Length exactly, including the case of a
	   trailing partial group. E4 supplies 10,000 rows; @Binary is capped at 8000 bytes, so at
	   most 2,000 are ever taken. */
	;WITH E1(n) AS (SELECT 1 FROM (VALUES(1),(1),(1),(1),(1),(1),(1),(1),(1),(1)) v(n)),
	      E2(n) AS (SELECT 1 FROM E1 a CROSS JOIN E1 b),
	      E4(n) AS (SELECT 1 FROM E2 a CROSS JOIN E2 b),
	      Positions(Ordinal) AS
	      (
	          SELECT TOP ((CONVERT(INT, @Length) + 3) / 4)
	                 ROW_NUMBER() OVER (ORDER BY (SELECT NULL))
	          FROM E4
	      )
	INSERT INTO @ContentItems(LocalPageID)
	SELECT SUBSTRING(@Binary, (Ordinal - 1) * 4 + 1, 4)
	FROM Positions

	/* We need to know which languages exist */
	UPDATE @ContentItems SET 
		LocalLanguageID = CASE WHEN fkLanguageBranchID IS NULL THEN fkMasterLanguageBranchID ELSE fkLanguageBranchID END
	FROM @ContentItems AS P
	INNER JOIN tblContent ON tblContent.pkID = P.LocalPageID
	LEFT JOIN tblContentLanguage ON P.LocalPageID = tblContentLanguage.fkContentID AND tblContentLanguage.fkLanguageBranchID = @LanguageBranchID

	/* Get all languages for all items*/
	SELECT tblContentLanguage.fkContentID as PageLinkID, tblContent.fkContentTypeID as PageTypeID, tblContentLanguage.fkLanguageBranchID as PageLanguageBranchID 
	FROM tblContentLanguage
	INNER JOIN @ContentItems on LocalPageID=tblContentLanguage.fkContentID
	INNER JOIN tblContent ON tblContent.pkID = tblContentLanguage.fkContentID
	ORDER BY tblContentLanguage.fkContentID

	/* Get all language versions that is requested (including master) */
	SELECT
		L.Status AS PageWorkStatus,
		L.fkContentID AS PageLinkID,
		NULL AS PageLinkWorkID,
		CASE AutomaticLink
			WHEN 1 THEN
				(CASE
					WHEN L.ContentLinkGUID IS NULL THEN 0	/* EPnLinkNormal */
					WHEN L.FetchData=1 THEN 4				/* EPnLinkFetchdata */
					ELSE 1								/* EPnLinkShortcut */
				END)
			ELSE
				(CASE
					WHEN L.LinkURL=N'#' THEN 3				/* EPnLinkInactive */
					ELSE 2								/* EPnLinkExternal */
				END)
		END AS PageShortcutType,
		L.ExternalURL AS PageExternalURL,
		L.ContentLinkGUID AS PageShortcutLinkID,
		L.Name AS PageName,
		L.URLSegment AS PageURLSegment,
		L.LinkURL AS PageLinkURL,
		L.BlobUri,
		L.ThumbnailUri,
		L.Created AS PageCreated,
		L.Changed AS PageChanged,
		L.Saved AS PageSaved,
		L.StartPublish AS PageStartPublish,
		L.StopPublish AS PageStopPublish,
		CASE WHEN L.Status = 4 THEN CAST(0 AS BIT) ELSE CAST(1 AS BIT) END AS PagePendingPublish,
		L.CreatorName AS PageCreatedBy,
		L.ChangedByName AS PageChangedBy,
		L.fkFrameID AS PageTargetFrame,
		0 AS PageChangedOnPublish,
		0 AS PageDelayedPublish,
		L.fkLanguageBranchID AS PageLanguageBranchID,
		L.DelayPublishUntil AS PageDelayPublishUntil
	FROM @ContentItems AS P
	INNER JOIN tblContentLanguage AS L ON LocalPageID=L.fkContentID
	WHERE L.fkLanguageBranchID = P.LocalLanguageID
	ORDER BY L.fkContentID

	IF (@@ROWCOUNT = 0)
	BEGIN
		RETURN
	END
		

/* Get data for page */
	SELECT
		LocalPageID AS PageLinkID,
		NULL AS PageLinkWorkID,
		fkParentID  AS PageParentLinkID,
		fkContentTypeID AS PageTypeID,
		NULL AS PageTypeName,
		CONVERT(INT,VisibleInMenu) AS PageVisibleInMenu,
		ChildOrderRule AS PageChildOrderRule,
		0 AS PagePeerOrderRule,	-- No longer used
		PeerOrder AS PagePeerOrder,
		CONVERT(NVARCHAR(38),tblContent.ContentGUID) AS PageGUID,
		ArchiveContentGUID AS PageArchiveLinkID,
		ContentAssetsID,
		ContentOwnerID,
		CONVERT(INT,Deleted) AS PageDeleted,
		DeletedBy AS PageDeletedBy,
		DeletedDate AS PageDeletedDate,
		fkMasterLanguageBranchID AS PageMasterLanguageBranchID,
		CreatorName
	FROM @ContentItems
	INNER JOIN tblContent ON LocalPageID=tblContent.pkID
	ORDER BY tblContent.pkID

	IF (@@ROWCOUNT = 0)
	BEGIN
		RETURN
	END

	
	/* Get the properties */
	/* NOTE! The CASE:s for LongString and Guid uses the precomputed LongStringLength to avoid 
	referencing LongString which may slow down the query */
	SELECT
		tblContentProperty.fkContentID AS PageLinkID,
		NULL AS PageLinkWorkID,
		tblPropertyDefinition.Name AS PropertyName,
		tblPropertyDefinition.pkID as PropertyDefinitionID,
		ScopeName,
		CONVERT(INT, Boolean) AS Boolean,
		Number AS IntNumber,
		FloatNumber,
		tblContentProperty.ContentType AS PageType,
		ContentLink,
		LinkGuid,	
		Date AS DateValue,
		String,
		(CASE 
			WHEN (@Threshold = 0) OR (COALESCE(LongStringLength, 2147483647) < @Threshold) THEN
				LongString
			ELSE
				NULL
		END) AS LongString,
		tblContentProperty.fkLanguageBranchID AS PageLanguageBranchID,
		(CASE 
			WHEN (@Threshold = 0) OR (COALESCE(LongStringLength, 2147483647) < @Threshold) THEN
				NULL
			ELSE
				guid
		END) AS Guid
	FROM @ContentItems AS P
	INNER JOIN tblContent ON tblContent.pkID=P.LocalPageID
	INNER JOIN tblContentProperty WITH (NOLOCK) ON tblContent.pkID=tblContentProperty.fkContentID --The join with tblContent ensures data integrity
	INNER JOIN tblPropertyDefinition ON tblPropertyDefinition.pkID=tblContentProperty.fkPropertyDefinitionID
	WHERE NOT tblPropertyDefinition.fkContentTypeID IS NULL AND
		(tblContentProperty.fkLanguageBranchID = P.LocalLanguageID
	OR
		tblPropertyDefinition.LanguageSpecific<3)
	ORDER BY tblContent.pkID

	/*Get category information*/
	SELECT 
		fkContentID AS PageLinkID,
		NULL AS PageLinkWorkID,
		fkCategoryID,
		CategoryType
	FROM tblContentCategory
	INNER JOIN @ContentItems ON LocalPageID=tblContentCategory.fkContentID
	WHERE CategoryType=0
	ORDER BY fkContentID,fkCategoryID

	/* Get access information */
	SELECT
		fkContentID AS PageLinkID,
		NULL AS PageLinkWorkID,
		tblContentAccess.Name,
		IsRole,
		AccessMask
	FROM
		@ContentItems
	INNER JOIN 
	    tblContentAccess ON LocalPageID=tblContentAccess.fkContentID
	ORDER BY
		fkContentID
END
GO
