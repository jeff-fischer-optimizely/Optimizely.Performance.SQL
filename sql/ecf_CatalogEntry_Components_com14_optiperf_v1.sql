/*
    dbo.ecf_CatalogEntry_Components_com14_optiperf_v1        OPT-0014        risk: Low

    Derived from EPiServer.Commerce.Core 14.46.0. The Commerce 14 counterpart to OPT-0005.

    WHY THIS IS A SEPARATE FILE
    ---------------------------
    Commerce 14's ecf_CatalogEntry_Components returns four result sets; Commerce 15's returns
    three. The variation branch in 14 selects from Variation and then again from Merchant:

        SELECT m.*
        FROM Merchant m
        INNER JOIN Variation v ON m.MerchantId = v.MerchantId
        INNER JOIN @CatalogEntryIds N ON N.ContentId = v.CatalogEntryId

    Commerce 15 dropped it. Pointing a Commerce 14 caller at the 15-derived copy would return
    one result set fewer than the reader expects, which is a correctness failure with no
    plausible recovery -- and a plan-shape change is the only thing this rewrite was supposed
    to be. The redirect gate catches it: the two versions hash differently, so a Commerce 14
    database can only ever match this entry and a Commerce 15 database only OPT-0005.

    WHAT CHANGED
    ------------
    OPTION (RECOMPILE) on all four statements. Below compatibility level 150 the
    @CatalogEntryIds table-valued parameter estimates at one row, so each gets a plan sized
    for a single entry while the catalog loader passes a page of them. Nothing else: same
    tables, same joins, same projections, same response-group branching, same four result
    sets in the same order.

    Low risk: a recompile directive cannot change which rows a statement returns.

    Preconditions: maximumCompatibilityLevel 140 -- at 150 and above the engine defers table
    variable compilation on its own and the hint is pure compile cost.
*/

IF OBJECT_ID('dbo.ecf_CatalogEntry_Components_com14_optiperf_v1', 'P') IS NOT NULL
    DROP PROCEDURE dbo.ecf_CatalogEntry_Components_com14_optiperf_v1;
GO

CREATE PROCEDURE [dbo].[ecf_CatalogEntry_Components_com14_optiperf_v1]
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

		/* OPTIPERF: present in Commerce 14 only. Retained deliberately -- see the header. */
		SELECT m.*
		FROM Merchant m
		INNER JOIN Variation v ON m.MerchantId = v.MerchantId
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
