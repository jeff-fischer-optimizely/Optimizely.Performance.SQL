/*
    dbo.GetListPropertiesOverThreshold_optiperf_v1        OPT-0006        risk: Moderate

    Derived from EPiServer.Cms.Core 12.24.1.

    Not redirected on its own -- nothing in the CMS calls this function directly. It is
    reachable only from dbo.netContentListPaged_optiperf_v1 (OPT-0002), and it exists as a
    separate object because the original is a separate object.

    WHAT THE ORIGINAL COMPUTES
    --------------------------
    Given a set of content items and a size threshold, it collects the scope names of
    oversized list properties and then reduces them to the *prefix-minimal* set: a scope is
    kept unless some other kept scope is a prefix of it.

    It does that with a WHILE loop. For each candidate, in ascending length order, it scans
    the accumulated results looking for a prefix match. The accumulator is a table variable
    with no index, so each scan is a full scan, and the loop is quadratic in the number of
    oversized list properties on the page. On content with a large nested block list -- the
    only content that reaches this code at all, since everything else is filtered out by
    the threshold -- that is where the time goes.

    WHAT CHANGED
    ------------
    The loop is replaced by one set-based statement. The equivalence argument:

      - Candidates are processed shortest-first, and a candidate is dropped only when an
        already-kept scope is a prefix of it.
      - Suppose scope T is a prefix of candidate S, and T was itself dropped. T can only
        have been dropped because some kept scope U is a prefix of T. Prefix is transitive,
        so U is also a prefix of S, and S is dropped either way.
      - Therefore "dropped because a *kept* scope prefixes it" and "dropped because *any*
        shorter scope prefixes it" select the same set, and the accumulator is not actually
        needed. The reduction is a single NOT EXISTS.

    Exact-length duplicates are handled by the original's LIKE matching itself; here they
    are handled by SELECT DISTINCT.

    WHY MODERATE AND NOT LOW
    ------------------------
    One case is not equivalent, and it is worth stating rather than burying.

    The prefix test is LIKE, not a substring comparison, so wildcard characters inside a
    scope name are interpreted. A scope name containing an underscore -- legal, and not
    unheard of in a property name -- can LIKE-match a *different* scope of the same length:
    'a_c' matches 'abc'. The original processes same-length candidates in insertion order
    and keeps whichever it reaches first, dropping the other. This version keeps both,
    because its NOT EXISTS only considers strictly shorter scopes and so cannot break the
    tie the way a sequential loop does.

    The consequence is a possible extra row in the returned set for content that has two
    equal-length sibling scopes where one LIKE-matches the other. Downstream in
    netContentListPaged that extra row can suppress a LongString that the original would
    have returned. That is a real, if narrow, behavioural difference on real data rather
    than a plan difference, which is what puts this entry above the Low tier.

    Preserving the tie-break exactly would mean reintroducing an ordering dependency and
    with it the loop, so the difference is accepted and recorded rather than fixed. Sites
    that use underscores in list property scope names should not enable OPT-0002.
*/

IF OBJECT_ID('dbo.GetListPropertiesOverThreshold_optiperf_v1', 'TF') IS NOT NULL
    DROP FUNCTION dbo.GetListPropertiesOverThreshold_optiperf_v1;
GO

CREATE FUNCTION [dbo].[GetListPropertiesOverThreshold_optiperf_v1]
(
	@ContentLanguages ContentLanguageTable READONLY,
    @Threshold        int
)
RETURNS @ScopedPropertiesTable TABLE
(
    ContentID int,
	ScopeName nvarchar(450)
)
AS
BEGIN
    DECLARE @DelayedScopes Table
    (
        ContentID int,
        ScopeName nvarchar(450),
        /* OPTIPERF: the original's accumulator was scanned once per candidate. This
           version is probed by the NOT EXISTS below, so it is worth indexing. */
        INDEX IX_DelayedScopes NONCLUSTERED (ContentID, ScopeName)
    );

	INSERT INTO @DelayedScopes(ContentID, ScopeName)
    SELECT
        prop.fkContentID as ContentID,
        left(ScopeName, len(ScopeName) - charindex('(', reverse(ScopeName)) + 1) as ScopeName
    from tblContentProperty as prop
    INNER JOIN @ContentLanguages cl on prop.fkContentID = cl.ContentID
    INNER JOIN tblPropertyDefinition as propdef on prop.fkPropertyDefinitionID = propdef.pkID
    AND prop.ListIndex IS NOT NULL
    AND COALESCE(prop.LongStringLength, 0) > @Threshold
    AND (prop.fkLanguageBranchID = cl.LanguageID OR prop.BranchSpecificScope = 0)
    OPTION (RECOMPILE)

    /* OPTIPERF: the original's WHILE loop, as one prefix-minimal reduction. */
    INSERT INTO @ScopedPropertiesTable(ContentID, ScopeName)
    SELECT DISTINCT d.ContentID, d.ScopeName
    FROM @DelayedScopes d
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM @DelayedScopes shorter
        WHERE shorter.ContentID = d.ContentID
          AND LEN(shorter.ScopeName) < LEN(d.ScopeName)
          AND d.ScopeName LIKE shorter.ScopeName + '%'
    )
    OPTION (RECOMPILE)

	RETURN
END
GO
