/*
    dbo.netContentDataLoad_cms11_optiperf_v1        OPT-0012        risk: Low

    Derived from EPiServer.Cms.Core 11.21.5. The CMS 11 counterpart to OPT-0004.

    Same rewrite, same reasoning: the prologue seeked tblContent by primary key up to three
    times to read two columns of a single row, and one seek reads both. Present in 361
    surveyed databases, which is nearly all of them -- this procedure loads a single content
    item and is called constantly.

    The CMS 11 and CMS 12 bodies differ by six lines, all in the property load, where CMS 12
    added BranchSpecificScope handling. The prologue this rewrite touches is character for
    character identical between the two, so the equivalence argument recorded in OPT-0004
    carries over without amendment. It is reproduced in the body comment rather than only
    referenced, because the guard it describes is the one thing here that is easy to drop.

    The remainder of the procedure -- the page data select, the language select, the property
    load with its CMS 11 tblProperty / tblPageDefinition naming, the category and access
    selects -- is reproduced unchanged.

    Low risk: one statement folded into another, no join or predicate altered, no dependence
    on the optimiser. Unconditional; there is no table variable here and nothing to gate on.
*/

IF OBJECT_ID('dbo.netContentDataLoad_cms11_optiperf_v1', 'P') IS NOT NULL
    DROP PROCEDURE dbo.netContentDataLoad_cms11_optiperf_v1;
GO

CREATE PROCEDURE [dbo].[netContentDataLoad_cms11_optiperf_v1]
(
	@ContentID	INT, 
	@LanguageBranchID INT
)
AS
BEGIN
	SET NOCOUNT ON

	DECLARE @ContentTypeID INT
	DECLARE @MasterLanguageID INT

	/* OPTIPERF: the original seeked tblContent by primary key up to three times to read two
	   columns of one row -- once for @ContentTypeID, conditionally once for the master
	   language fallback, and once more for @MasterLanguageID. One seek reads both.

	   The @MasterLanguageID IS NOT NULL guard is load-bearing. When no row exists for
	   @ContentID the original's conditional SELECT assigns nothing and leaves
	   @LanguageBranchID at the caller's value; without the guard the folded form would
	   overwrite it with NULL. The procedure returns 0 shortly afterwards in that case, but
	   the variable is read before then.

	   @ContentTypeID is never read by the rest of the procedure. It is kept so that a diff
	   against the original does not raise a question that has to be re-answered. */
	SELECT
		@ContentTypeID = tblContent.fkContentTypeID,
		@MasterLanguageID = tblContent.fkMasterLanguageBranchID
	FROM tblContent
	WHERE tblContent.pkID = @ContentID

	/*This procedure should always return a page (if exist), preferable in requested language else in master language*/
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
		-- RTRIM(tblContentLanguage.fkLanguageID) AS PageLanguageID,
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
		OR (tblProperty.fkLanguageBranchID = @MasterLanguageID AND tblPageDefinition.LanguageSpecific < 3))

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
