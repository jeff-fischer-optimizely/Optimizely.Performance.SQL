/*
    dbo.ecf_CatalogEntry_List_com14_optiperf_v1        OPT-0015        risk: Low

    Derived from EPiServer.Commerce.Core 14.46.0. The Commerce 14 counterpart to OPT-0008.
    Requires dbo.ecf_CatalogEntry_Components_com14_optiperf_v1 (OPT-0014) to be deployed first.

    WHY THIS IS A SEPARATE FILE WHEN THE BODY IS THE SAME
    -----------------------------------------------------
    ecf_CatalogEntry_List is byte for byte identical in Commerce 14 and 15. What differs is
    the procedure it calls: Commerce 14's ecf_CatalogEntry_Components returns an extra
    Merchant result set. So this copy must call the Commerce 14 components replacement, and
    OPT-0008 must call the Commerce 15 one, even though the two loaders are the same text.

    That breaks the usual way a redirect knows which product version it is looking at. The
    body hash of the procedure being replaced is identical here, so it cannot discriminate.
    The entry therefore gates on the callee instead, via the requiredProcedureBodies
    precondition: this one applies where ecf_CatalogEntry_Components hashes to the Commerce
    14 body, OPT-0008 where it hashes to the Commerce 15 body. Those two conditions cannot
    both hold, so the pair is mutually exclusive by construction rather than by ordering --
    a database with both sets of scripts deployed still resolves correctly.

    WHAT CHANGED
    ------------
    OPTION (RECOMPILE) on the four statements that join the @CatalogEntries table-valued
    parameter, and the components call pointed at the versioned Commerce 14 copy so the same
    treatment continues into it. No join, predicate or projection altered; ORDER BY
    r.SortOrder on the first statement is the original's.

    Low risk: recompile directives cannot change the rows returned, and the procedure it
    calls is the one whose equivalence argument is recorded in OPT-0014.

    Preconditions: maximumCompatibilityLevel 140; ecf_CatalogEntry_Components must hash to
    the Commerce 14 body; ecf_CatalogEntry_Components_com14_optiperf_v1 must be deployed.
*/

IF OBJECT_ID('dbo.ecf_CatalogEntry_List_com14_optiperf_v1', 'P') IS NOT NULL
    DROP PROCEDURE dbo.ecf_CatalogEntry_List_com14_optiperf_v1;
GO

CREATE PROCEDURE [dbo].[ecf_CatalogEntry_List_com14_optiperf_v1]
    @CatalogEntries dbo.udttEntityList READONLY,
	@ResponseGroup INT = NULL
AS
BEGIN
	SELECT n.*
	FROM CatalogEntry n
	JOIN @CatalogEntries r ON n.CatalogEntryId = r.EntityId
	ORDER BY r.SortOrder
	OPTION (RECOMPILE)	/* OPTIPERF */

	SELECT s.*
	FROM CatalogItemSeo s
	JOIN @CatalogEntries r ON s.CatalogEntryId = r.EntityId
	OPTION (RECOMPILE)	/* OPTIPERF */

	IF @ResponseGroup IS NULL
	BEGIN
		SELECT er.CatalogId, er.CatalogEntryId, er.CatalogNodeId, er.SortOrder, er.IsPrimary
		FROM NodeEntryRelation er
		JOIN @CatalogEntries r ON er.CatalogEntryId = r.EntityId
		OPTION (RECOMPILE)	/* OPTIPERF */
	END

	DECLARE @CatalogEntryIds udttContentList
	INSERT INTO @CatalogEntryIds
	SELECT EntityId from @CatalogEntries
	OPTION (RECOMPILE)	/* OPTIPERF */

	/* OPTIPERF: the Commerce 14 versioned copy -- see OPT-0014. */
	exec ecf_CatalogEntry_Components_com14_optiperf_v1 @CatalogEntryIds, @ResponseGroup
END
GO
