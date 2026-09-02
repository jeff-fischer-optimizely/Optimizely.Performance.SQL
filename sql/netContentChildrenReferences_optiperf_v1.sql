/*
    dbo.netContentChildrenReferences_optiperf_v1        OPT-0009        risk: Low

    Derived from EPiServer.Cms.Core 12.24.1.

    THE LOWER() CASE
    ----------------
    This procedure contains the one genuinely non-sargable LOWER() in the CMS schema:

        SELECT @LanguageBranchID = pkID FROM tblLanguageBranch
        WHERE LOWER(LanguageID) = LOWER(@LanguageID)

    A function on the column side means no seek is possible, so the predicate scans. On a
    case-insensitive collation -- which every surveyed database has -- both LOWER() calls
    are redundant: LanguageID = @LanguageID already compares case-insensitively, and the
    rewritten predicate is sargable.

    Be clear about the size of this, because the premise is more attractive than the
    payoff: tblLanguageBranch holds one row per configured language. A dozen rows, one
    page. Turning a twelve-row scan into a seek saves essentially nothing. The change is
    made because the procedure is being copied anyway and there is no reason to carry a
    non-sargable predicate forward -- not because it is where this procedure's cost lives.

    It is worth recording where the LOWER() premise led, since it was the motivating idea
    for a whole low-risk tier. Every LOWER() in the CMS and Commerce schemas was examined.
    Nearly all are either scalar assignments -- SET @x = LOWER(@param), evaluated once and
    fully sargable thereafter -- or predicates against LoweredUserName and LoweredRoleName,
    denormalised columns Optimizely maintains precisely so the predicate stays sargable.
    Those are already optimal. Across all four shipped packages exactly two column-side
    LOWER() predicates exist: this one, and one in a Commerce metaclass DDL procedure that
    runs at deployment time against system tables. The tier turned out to be one query.

    WHERE THE COST ACTUALLY IS
    --------------------------
    139 surveyed databases. The cost is the child listing itself -- eight near-identical
    branches selecting children of @ParentID ordered by Created, Name, PeerOrder,
    StartPublish or Changed. IDX_tblContent_fkParentID already covers the seek and carries
    the right INCLUDE list, so the remaining work is the sort, which is proportional to the
    number of children and is inherent to the request. There is no rewrite for that; a
    content tree with tens of thousands of children under one parent is an editorial
    problem, not a query problem. The eight branches are reproduced unchanged.

    WHY LOW RISK
    ------------
    Gated on requiresCaseInsensitiveCollation. Given that precondition the two predicates
    are indistinguishable: for every pair of values, LOWER(a) = LOWER(b) and a = b agree
    under a CI collation. If the probe cannot establish the collation, or establishes a
    case-sensitive one, the rewrite is skipped and Optimizely's procedure runs.

    Accent sensitivity is deliberately not required. LOWER() does not fold accents, so the
    original and the rewrite behave identically on an accent-sensitive collation -- adding
    the requirement would exclude databases the rewrite is correct on.

    The fallback path matters and is preserved. When no language branch matches, the
    original relies on @@ROWCOUNT < 1 immediately after the SELECT to take a different
    branch. @@ROWCOUNT is unaffected by making the predicate sargable, and the check
    remains the statement directly following the SELECT, which is the only way it stays
    correct.
*/

IF OBJECT_ID('dbo.netContentChildrenReferences_optiperf_v1', 'P') IS NOT NULL
    DROP PROCEDURE dbo.netContentChildrenReferences_optiperf_v1;
GO

CREATE PROCEDURE [dbo].[netContentChildrenReferences_optiperf_v1]
(
	@ParentID INT,
	@LanguageID NCHAR(17),
	@ChildOrderRule INT OUTPUT
)
AS
BEGIN
	SET NOCOUNT ON
/*
		CreatedDescending		= 1,
		CreatedAscending		= 2,
		Alphabetical			= 3,
		Index					= 4,
		ChangedDescending		= 5,
		Rank					= 6,
		PublishedAscending		= 7,
		PublishedDescending		= 8
*/
	SELECT @ChildOrderRule = ChildOrderRule FROM tblContent WHERE pkID=@ParentID

	IF (@ChildOrderRule = 1)
	BEGIN
		SELECT
			pkID AS ContentLinkID, ContentType, fkContentTypeID as ContentTypeID, IsLeafNode
		FROM
			tblContent
		INNER JOIN
			tblContentLanguage ON tblContentLanguage.fkContentID=tblContent.pkID
		WHERE
			fkParentID=@ParentID AND tblContent.fkMasterLanguageBranchID=tblContentLanguage.fkLanguageBranchID
		ORDER BY
			Created DESC,ContentLinkID DESC

		RETURN @@ROWCOUNT
	END

	IF (@ChildOrderRule = 2)
	BEGIN
		SELECT
			pkID AS ContentLinkID, ContentType, fkContentTypeID as ContentTypeID, IsLeafNode
		FROM
			tblContent
		INNER JOIN
			tblContentLanguage ON tblContentLanguage.fkContentID=tblContent.pkID
		WHERE
			fkParentID=@ParentID AND tblContent.fkMasterLanguageBranchID=tblContentLanguage.fkLanguageBranchID
		ORDER BY
			Created ASC,ContentLinkID ASC

		RETURN @@ROWCOUNT
	END

	IF (@ChildOrderRule = 3)
	BEGIN
		-- Get language branch for listing since we want to sort on name
		DECLARE @LanguageBranchID INT
		/* OPTIPERF: LOWER() dropped from both sides. Sargable, and identical under the
		   case-insensitive collation this rewrite is gated on. The @@ROWCOUNT test below
		   must stay immediately after this statement. */
		SELECT
			@LanguageBranchID = pkID
		FROM
			tblLanguageBranch
		WHERE
			LanguageID = @LanguageID

		-- If we did not find a valid language branch, go with master language branch from tblContent
		IF (@@ROWCOUNT < 1)
		BEGIN
			SELECT
			pkID AS ContentLinkID, ContentType, fkContentTypeID as ContentTypeID, IsLeafNode
			FROM
				tblContent
			INNER JOIN
				tblContentLanguage ON tblContentLanguage.fkContentID=tblContent.pkID
			WHERE
				fkParentID=@ParentID AND tblContent.fkMasterLanguageBranchID=tblContentLanguage.fkLanguageBranchID
			ORDER BY
				Name ASC

		    RETURN @@ROWCOUNT
		END

		SELECT
			pkID AS ContentLinkID, ContentType, fkContentTypeID as ContentTypeID, IsLeafNode
		FROM
			tblContent AS P
		LEFT JOIN
			tblContentLanguage AS PL ON PL.fkContentID=P.pkID AND
			PL.fkLanguageBranchID=@LanguageBranchID
		WHERE
			P.fkParentID=@ParentID
		ORDER BY
			PL.Name ASC

		RETURN @@ROWCOUNT
	END

	IF (@ChildOrderRule = 4)
	BEGIN
		SELECT
			pkID AS ContentLinkID, ContentType, fkContentTypeID as ContentTypeID, IsLeafNode
		FROM
			tblContent
		WHERE
			fkParentID=@ParentID
		ORDER BY
			PeerOrder ASC

		RETURN @@ROWCOUNT
	END

	IF (@ChildOrderRule = 5)
	BEGIN
		SELECT
			pkID AS ContentLinkID, ContentType, fkContentTypeID as ContentTypeID, IsLeafNode
		FROM
			tblContent
		INNER JOIN
			tblContentLanguage ON tblContentLanguage.fkContentID=tblContent.pkID
		WHERE
			fkParentID=@ParentID AND tblContent.fkMasterLanguageBranchID=tblContentLanguage.fkLanguageBranchID
		ORDER BY
			Changed DESC

		RETURN @@ROWCOUNT
	END

	IF (@ChildOrderRule = 7)
	BEGIN
		SELECT
			pkID AS ContentLinkID, ContentType, fkContentTypeID as ContentTypeID, IsLeafNode
		FROM
			tblContent
		INNER JOIN
			tblContentLanguage ON tblContentLanguage.fkContentID=tblContent.pkID
		WHERE
			fkParentID=@ParentID AND tblContent.fkMasterLanguageBranchID=tblContentLanguage.fkLanguageBranchID
		ORDER BY
			StartPublish ASC

		RETURN @@ROWCOUNT
	END

	IF (@ChildOrderRule = 8)
	BEGIN
		SELECT
			pkID AS ContentLinkID, ContentType, fkContentTypeID as ContentTypeID, IsLeafNode
		FROM
			tblContent
		INNER JOIN
			tblContentLanguage ON tblContentLanguage.fkContentID=tblContent.pkID
		WHERE
			fkParentID=@ParentID AND tblContent.fkMasterLanguageBranchID=tblContentLanguage.fkLanguageBranchID
		ORDER BY
			StartPublish DESC

		RETURN @@ROWCOUNT
	END

END
GO
