/*
    dbo.netContentDataLoad_optiperf_v1        OPT-0004        risk: Low

    Derived from EPiServer.Cms.Core 12.24.1.

    The CMS 11 body differs by six lines, all in the property load, so it gets its own copy
    and its own hash: see netContentDataLoad_cms11_optiperf_v1.sql (OPT-0012). The prologue
    this rewrite touches is character for character identical between the two.

    WHAT CHANGED
    ------------
    The original reads tblContent four times for the same @ContentID:

        1.  SELECT @ContentTypeID    = fkContentTypeID          WHERE pkID = @ContentID
        2.  SELECT @LanguageBranchID = fkMasterLanguageBranchID WHERE pkID = @ContentID   (conditional)
        3.  SELECT @MasterLanguageID = fkMasterLanguageBranchID WHERE pkID = @ContentID
        4.  the main SELECT                                     WHERE pkID = @ContentID

    Reads 1-3 are three separate clustered index seeks that fetch two columns from one
    row. This copy folds them into a single seek and assigns from local variables.

    Nothing else is touched. The five result sets are byte-for-byte the statements
    Optimizely wrote.

    WHY THIS IS WORTH DOING
    -----------------------
    Per call it saves two trivial seeks, which is not interesting. What makes it
    interesting is the multiplier: this is the single most widely executed procedure in
    the surveyed estate, present in 361 databases, and the survey window recorded it in
    the billions of executions. Two saved seeks per call at that volume is a large
    absolute number made of individually negligible pieces, which is exactly the kind of
    cost that never gets attention because no single execution looks bad in a trace.

    WHY IT IS LOW RISK
    ------------------
    @ContentTypeID is assigned and then never read, in the original or here. It is
    retained only so that a reader diffing the two bodies does not have to wonder whether
    its removal mattered.

    The one behavioural subtlety is the fallback assignment. In the original, when
    @ContentID matches no row, statement 2 assigns nothing and @LanguageBranchID keeps the
    caller's value. A naive fold to

        SET @LanguageBranchID = @MasterLanguageID

    would instead set it to NULL, which differs. The guard below preserves the original
    exactly: the assignment happens only when a row was found. In that case the procedure
    goes on to return no rows and RETURN 0 regardless, so the distinction is unobservable
    from outside -- but it is preserved rather than argued away, because "unobservable"
    is a claim about today's callers and the guard costs nothing.

    Preconditions: none. This rewrite depends on no collation, index or optimiser
    behaviour, which is why it is the only entry in the catalogue that ships
    unconditionally.
*/

IF OBJECT_ID('dbo.netContentDataLoad_optiperf_v1', 'P') IS NOT NULL
    DROP PROCEDURE dbo.netContentDataLoad_optiperf_v1;
GO

CREATE PROCEDURE [dbo].[netContentDataLoad_optiperf_v1]
(
	@ContentID	INT,
	@LanguageBranchID INT
)
AS
BEGIN
	SET NOCOUNT ON

	DECLARE @ContentTypeID INT
	DECLARE @MasterLanguageID INT

	/* OPTIPERF: one seek in place of the original's three. */
	SELECT
		@ContentTypeID = tblContent.fkContentTypeID,
		@MasterLanguageID = tblContent.fkMasterLanguageBranchID
	FROM tblContent
	WHERE tblContent.pkID = @ContentID

	/*This procedure should always return a page (if exist), preferable in requested language else in master language*/
	/* OPTIPERF: @MasterLanguageID IS NOT NULL stands in for "the tblContent row exists",
	   which is what made the original's SELECT-assignment a no-op for a missing row. */
	IF (@MasterLanguageID IS NOT NULL
		AND (@LanguageBranchID = -1
			OR NOT EXISTS (SELECT 1 FROM tblContentLanguage WHERE fkContentID=@ContentID AND fkLanguageBranchID = @LanguageBranchID)))
		SET @LanguageBranchID = @MasterLanguageID

	/* Get data for page */
	SELECT
		tblContent.pkID AS PageLinkID,
		NULL AS PageLinkWorkID,
		fkParentID  AS PageParentLinkID,
		fkContentTypeID AS PageTypeID,
		NULL AS PageTypeName,
		CONVERT(INT,VisibleInMenu) AS PageVisibleInMenu,
		ChildOrderRule AS PageChildOrderRule,
		PeerOrder AS PagePeerOrder,
		CONVERT(NVARCHAR(38),tblContent.ContentGUID) AS PageGUID,
		ArchiveContentGUID AS PageArchiveLinkID,
		ContentAssetsID,
		ContentOwnerID,
		CONVERT(INT,Deleted) AS PageDeleted,
		DeletedBy AS PageDeletedBy,
		DeletedDate AS PageDeletedDate,
		(SELECT ChildOrderRule FROM tblContent AS ParentPage WHERE ParentPage.pkID=tblContent.fkParentID) AS PagePeerOrderRule,
		fkMasterLanguageBranchID AS PageMasterLanguageBranchID,
		CreatorName
	FROM tblContent
	WHERE tblContent.pkID=@ContentID

	IF (@@ROWCOUNT = 0)
		RETURN 0

	/* Get data for page languages */
	SELECT
		L.fkContentID AS PageID,
		CASE L.AutomaticLink
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
		L.Status as PageWorkStatus,
		L.DelayPublishUntil AS PageDelayPublishUntil
	FROM tblContentLanguage AS L
	WHERE L.fkContentID=@ContentID
		AND L.fkLanguageBranchID=@LanguageBranchID

	/* Get the property data for the requested language */
	SELECT
		tblPageDefinition.Name AS PropertyName,
		tblPageDefinition.pkID as PropertyDefinitionID,
		ScopeName,
		CONVERT(INT, Boolean) AS Boolean,
		Number AS IntNumber,
		FloatNumber,
		PageType,
		PageLink AS ContentLink,
		LinkGuid,
		Date AS DateValue,
		String,
		LongString,
		tblProperty.fkLanguageBranchID AS LanguageBranchID
	FROM tblProperty
	INNER JOIN tblPageDefinition ON tblPageDefinition.pkID = tblProperty.fkPageDefinitionID
	WHERE tblProperty.fkPageID=@ContentID AND NOT tblPageDefinition.fkPageTypeID IS NULL
		AND (tblProperty.fkLanguageBranchID = @LanguageBranchID
	    OR (tblProperty.fkLanguageBranchID = @MasterLanguageID AND
            ((tblProperty.BranchSpecificScope IS NULL AND tblPageDefinition.LanguageSpecific < 3) OR tblProperty.BranchSpecificScope = 0)))

	/*Get category information*/
	SELECT fkPageID AS PageID,fkCategoryID,CategoryType
	FROM tblCategoryPage
	WHERE fkPageID=@ContentID AND CategoryType=0
	ORDER BY fkCategoryID

	/* Get access information */
	SELECT
		fkContentID AS PageID,
		Name,
		IsRole,
		AccessMask
	FROM
		tblContentAccess
	WHERE
	    fkContentID=@ContentID
	ORDER BY
	    IsRole DESC,
		Name

	/* Get all languages for the page */
	SELECT fkLanguageBranchID as PageLanguageBranchID FROM tblContentLanguage
		WHERE tblContentLanguage.fkContentID=@ContentID

RETURN 0
END
GO
