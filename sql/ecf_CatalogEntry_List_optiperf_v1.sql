/*
    dbo.ecf_CatalogEntry_List_optiperf_v1        OPT-0008        risk: Low

    Derived from EPiServer.Commerce.Core 15.1.0. Commerce 15 only.
    Requires dbo.ecf_CatalogEntry_Components_optiperf_v1 (OPT-0005) to be deployed first.

    The loader body is identical in Commerce 14 and 15; the components procedure it calls is
    not. So this entry gates on the callee rather than on its own hash, which cannot tell the
    two versions apart: it applies where ecf_CatalogEntry_Components hashes to the Commerce 15
    body, and OPT-0015 covers the Commerce 14 case. See OPT-0015 for the full reasoning.

    The catalog entry batch loader: 100 surveyed databases. Four statements join the
    @CatalogEntries table-valued parameter, and one of them copies it into a second table
    variable before handing it to the components procedure. Below compatibility level 150
    every one of those is planned for a single row.

    OPTION (RECOMPILE) on all four, and the call redirected to the versioned components
    copy so the same treatment continues into it. No join, predicate or projection altered;
    ORDER BY r.SortOrder on the first statement is the original's.

    Low risk: recompile directives cannot change the rows returned, and calling the
    versioned components procedure is a call to a body whose own equivalence argument is
    recorded in OPT-0005.

    Preconditions: maximumCompatibilityLevel 140.
*/

IF OBJECT_ID('dbo.ecf_CatalogEntry_List_optiperf_v1', 'P') IS NOT NULL
    DROP PROCEDURE dbo.ecf_CatalogEntry_List_optiperf_v1;
GO

CREATE PROCEDURE [dbo].[ecf_CatalogEntry_List_optiperf_v1]
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

	/* OPTIPERF: the versioned copy -- see OPT-0005. */
	exec ecf_CatalogEntry_Components_optiperf_v1 @CatalogEntryIds, @ResponseGroup
END
GO
