/*
    dbo.ecf_NodeEntryRelations_optiperf_v1        OPT-0007        risk: Low

    Derived from EPiServer.Commerce.Core 15.1.0 / 14.46.0.

    One statement, one table-valued parameter join, 108 surveyed databases and over half a
    billion executions in the survey window. There is nothing wrong with the query; it is
    the table variable estimate that is wrong, and only below compatibility level 150.

    OPTION (RECOMPILE), and nothing else.

    Low risk: a recompile directive cannot change the rows returned.
    Preconditions: maximumCompatibilityLevel 140.
*/

IF OBJECT_ID('dbo.ecf_NodeEntryRelations_optiperf_v1', 'P') IS NOT NULL
    DROP PROCEDURE dbo.ecf_NodeEntryRelations_optiperf_v1;
GO

CREATE PROCEDURE [dbo].[ecf_NodeEntryRelations_optiperf_v1]
	@ContentList udttContentList readonly
AS
BEGIN
	Select NodeEntryRelation.CatalogId, CatalogEntryId, CatalogNodeId, SortOrder, IsPrimary
	FROM NodeEntryRelation
	INNER JOIN @ContentList as idTable on idTable.ContentId = NodeEntryRelation.CatalogEntryId
	OPTION (RECOMPILE)	/* OPTIPERF */
END
GO
