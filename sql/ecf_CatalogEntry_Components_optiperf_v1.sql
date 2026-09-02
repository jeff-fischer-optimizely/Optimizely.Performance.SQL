/*
    dbo.ecf_CatalogEntry_Components_optiperf_v1        OPT-0005        risk: Low

    Derived from EPiServer.Commerce.Core 15.1.0. Commerce 15 only -- Commerce 14's copy of
    this procedure returns an extra Merchant result set in the variation branch, so it needs
    a separately derived body. That is OPT-0014. The two hash differently, so a database can
    only ever match the entry written for the version it is running.

    Three statements, each joining the @CatalogEntryIds table-valued parameter to a catalog
    table. Present in 100 surveyed databases and executed in the hundreds of millions.

    Below compatibility level 150 a table-valued parameter is a table variable and estimates
    at one row, so all three get nested-loop plans sized for a single entry while the
    catalog loader passes a page of them. OPTION (RECOMPILE) compiles each against the row
    count actually supplied.

    Nothing else changed: same tables, same joins, same projections, same response-group
    branching.

    Low risk for the reason every hint-only entry is: a recompile directive cannot change
    which rows a statement returns.

    Preconditions: maximumCompatibilityLevel 140 -- at 150 and above the engine defers
    table variable compilation on its own and the hint is pure compile cost.
*/

IF OBJECT_ID('dbo.ecf_CatalogEntry_Components_optiperf_v1', 'P') IS NOT NULL
    DROP PROCEDURE dbo.ecf_CatalogEntry_Components_optiperf_v1;
GO

CREATE PROCEDURE [dbo].[ecf_CatalogEntry_Components_optiperf_v1]
	@CatalogEntryIds udttContentList readonly,
	@ResponseGroup INT = NULL
AS
BEGIN
	DECLARE @CatalogEntryFull INT
	DECLARE @Associations INT
	DECLARE @Assets INT
	DECLARE @Variations INT

	SET @CatalogEntryFull = 4
	SET @Associations = 8
	SET @Assets = 32
	SET @Variations = 128

	IF ((@ResponseGroup & @Variations = @Variations)
		OR (@ResponseGroup & @CatalogEntryFull = @CatalogEntryFull))
	BEGIN
		SELECT v.*
		FROM Variation v
		INNER JOIN @CatalogEntryIds N ON N.ContentId = v.CatalogEntryId
		OPTION (RECOMPILE)	/* OPTIPERF */
    END

	IF ((@ResponseGroup & @Associations = @Associations)
		OR (@ResponseGroup & @CatalogEntryFull = @CatalogEntryFull))
	BEGIN
		SELECT a.*
		FROM CatalogAssociation a
		INNER JOIN @CatalogEntryIds N ON N.ContentId = a.CatalogEntryId
		OPTION (RECOMPILE)	/* OPTIPERF */
	END

	IF ((@ResponseGroup & @Assets = @Assets)
		OR (@ResponseGroup & @CatalogEntryFull = @CatalogEntryFull))
	BEGIN
		SELECT a.*
		FROM CatalogItemAsset a
		INNER JOIN @CatalogEntryIds N ON N.ContentId = a.CatalogEntryId
		OPTION (RECOMPILE)	/* OPTIPERF */
	END

END
GO
