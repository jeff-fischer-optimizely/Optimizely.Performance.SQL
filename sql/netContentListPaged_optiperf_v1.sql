/*
    dbo.netContentListPaged_optiperf_v1        OPT-0002        risk: Moderate

    Derived from EPiServer.Cms.Core 12.24.1.
    Requires dbo.GetListPropertiesOverThreshold_optiperf_v1 (OPT-0006) to be deployed first.

    THE PROBLEM
    -----------
    This is the batch content loader, and it is the second largest single item in the
    surveyed estate: present in 185 databases, and the survey attributes around a tenth of
    all fleet logical reads to it across twelve distinct statements.

    Every one of those statements joins @ContentItems, a table variable. Before
    compatibility level 150 a table variable is estimated at one row, whatever it actually
    holds. The estimate is not slightly wrong; it is wrong by the batch size. A loader
    called with two hundred content IDs gets a plan built for one, which means nested loops
    and per-row seeks all the way down, repeated two hundred times, against tblContent,
    tblContentLanguage, tblContentProperty and tblContentAccess in turn.

    This is not a defect in the procedure. It is the documented behaviour of table
    variables on the optimiser that half the surveyed estate is still running.

    WHAT CHANGED
    ------------
    OPTION (RECOMPILE) on the seven statements that join @ContentItems, plus the switch to
    the optimised list-property function. Nothing else. No join was reordered, no predicate
    rewritten, no column added or removed.

    With the hint, the statement is compiled at execution time, when the row count of the
    table variable is known, and the plan is sized for the batch actually in hand.

    WHY MODERATE AND NOT LOW
    ------------------------
    The hints themselves are Low, and by the usual argument: OPTION (RECOMPILE) is a
    compilation directive, it cannot change which rows a statement returns or the order it
    returns them in, and there is no input for which the hinted and unhinted statements
    differ. That holds by inspection rather than by measurement.

    The rating is Moderate anyway, because of the other half of the change. This procedure
    calls GetListPropertiesOverThreshold, and the versioned copy it is pointed at is OPT-0006,
    which is Moderate: its set-based prefix reduction is not equivalent to the original loop
    for scope names containing a LIKE wildcard. An underscore in a list property scope name
    is enough to produce a different set of rows.

    Risk does not stay behind a procedure call. A caller is at least as risky as what it
    calls, so this entry inherits OPT-0006's tier rather than claiming its own. Anyone
    reading only this header should still see the reason they need to check their scope
    names, which is why it is restated rather than referenced.

    The compile cost is real but bounded: each execution pays for one. For a procedure whose
    unhinted plans are mis-sized by two orders of magnitude that trade is heavily favourable,
    but it is a trade, and it is why the entry is gated rather than unconditional.

    PRECONDITIONS
    -------------
    maximumCompatibilityLevel 140.

    At level 150 and above the engine defers table variable compilation on its own and
    arrives at the same cardinality without the hint. Applying this there would buy nothing
    and still charge the compile on every call -- a regression, not a fix. The ceiling means
    a database upgraded to 150 stops receiving the redirect at its next capability probe,
    with no configuration change and no redeployment.

    NOT DONE HERE
    -------------
    Replacing @ContentItems with a #temp table would fix the estimate without a per-call
    compile, and would carry real column statistics rather than just a row count. It is
    the better long-term answer and it is deliberately not in v1: it changes tempdb
    behaviour, it interacts with the caller's transaction scope, and it cannot be argued
    correct by inspection the way a hint can. Recorded in approvals/OPT-0002 as the
    candidate for v2, to be measured rather than reasoned about.
*/

IF OBJECT_ID('dbo.netContentListPaged_optiperf_v1', 'P') IS NOT NULL
    DROP PROCEDURE dbo.netContentListPaged_optiperf_v1;
GO

CREATE PROCEDURE dbo.netContentListPaged_optiperf_v1
(
	@Ids IDTable READONLY,
	@Threshold INT = 0,
	@LanguageBranchID INT
)
AS
BEGIN
	SET NOCOUNT ON

	DECLARE @ContentItems ContentLanguageTable
    INSERT INTO @ContentItems(ContentID) SELECT ID FROM @Ids

	/* We need to know which languages exist */
	UPDATE @ContentItems SET
		LanguageID = CASE WHEN fkLanguageBranchID IS NULL THEN fkMasterLanguageBranchID ELSE fkLanguageBranchID END
	FROM @ContentItems AS P
	INNER JOIN tblContent ON tblContent.pkID = P.ContentID
	LEFT JOIN tblContentLanguage ON P.ContentID = tblContentLanguage.fkContentID AND tblContentLanguage.fkLanguageBranchID = @LanguageBranchID
	OPTION (RECOMPILE)	/* OPTIPERF */

	/* Get all languages for all items*/
	SELECT tblContentLanguage.fkContentID as PageLinkID, tblContent.fkContentTypeID as PageTypeID, tblContentLanguage.fkLanguageBranchID as PageLanguageBranchID
	FROM tblContentLanguage
	INNER JOIN @ContentItems on ContentID=tblContentLanguage.fkContentID
	INNER JOIN tblContent ON tblContent.pkID = tblContentLanguage.fkContentID
	ORDER BY tblContentLanguage.fkContentID
	OPTION (RECOMPILE)	/* OPTIPERF */

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
	INNER JOIN tblContentLanguage AS L ON ContentID=L.fkContentID
	WHERE L.fkLanguageBranchID = P.LanguageID
	ORDER BY L.fkContentID
	OPTION (RECOMPILE)	/* OPTIPERF */

	IF (@@ROWCOUNT = 0)
	BEGIN
		RETURN
	END


/* Get data for page */
	SELECT
		ContentID AS PageLinkID,
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
	INNER JOIN tblContent ON ContentID=tblContent.pkID
	ORDER BY tblContent.pkID
	OPTION (RECOMPILE)	/* OPTIPERF */

	IF (@@ROWCOUNT = 0)
	BEGIN
		RETURN
	END;

    WITH DelayedScopes (ContentID , Scope)
    AS
    /* OPTIPERF: the versioned copy of the function -- see OPT-0006. */
    (SELECT ContentID, ScopeName from GetListPropertiesOverThreshold_optiperf_v1(@ContentItems, @Threshold))

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
		END) AS Guid,
        (CASE
			WHEN (ScopeName IS NOT NULL AND ListIndex IS NOT NULL AND ds.Scope IS NOT NULL) THEN
				ds.Scope
			ELSE
				NULL
		END) AS DelayedScope
	FROM @ContentItems AS P
	INNER JOIN tblContent ON tblContent.pkID=P.ContentID
	INNER JOIN tblContentProperty WITH (NOLOCK) ON tblContent.pkID=tblContentProperty.fkContentID --The join with tblContent ensures data integrity
	INNER JOIN tblPropertyDefinition ON tblPropertyDefinition.pkID=tblContentProperty.fkPropertyDefinitionID
    LEFT JOIN DelayedScopes ds ON (P.ContentID = ds.ContentID AND tblContentProperty.ScopeName LIKE ds.Scope + '%')
	WHERE NOT tblPropertyDefinition.fkContentTypeID IS NULL AND
		(tblContentProperty.fkLanguageBranchID = P.LanguageID
	OR
		((tblContentProperty.BranchSpecificScope IS NULL AND tblPropertyDefinition.LanguageSpecific < 3) OR tblContentProperty.BranchSpecificScope = 0))
    AND (ScopeName is NULL OR ListIndex IS NULL OR ListIndex = 0 OR ds.Scope IS NULL)
	ORDER BY tblContent.pkID
	OPTION (RECOMPILE);	/* OPTIPERF */

	/*Get category information*/
	SELECT
		fkContentID AS PageLinkID,
		NULL AS PageLinkWorkID,
		fkCategoryID,
		CategoryType
	FROM tblContentCategory
	INNER JOIN @ContentItems ON ContentID=tblContentCategory.fkContentID
	WHERE CategoryType=0
	ORDER BY fkContentID,fkCategoryID
	OPTION (RECOMPILE)	/* OPTIPERF */

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
	    tblContentAccess ON ContentID=tblContentAccess.fkContentID
	ORDER BY
		fkContentID
	OPTION (RECOMPILE)	/* OPTIPERF */
END
GO
