# Replacement procedures

Every file here creates a **new** database object. Nothing in this directory alters,
drops or replaces an object that Optimizely ships.

That is the whole design. Optimizely owns `dbo.netContentDataLoad`; a CMS upgrade will
rewrite it without asking, and a support engineer looking at a misbehaving site has to be
able to trust that the procedure they are reading is the one Optimizely published. So the
optimised copy is deployed beside the original under a versioned name —
`dbo.netContentDataLoad_optiperf_v1` — and the shim rewrites the *call* at the client, not
the *procedure* on the server.

The consequences are worth being explicit about, because they are the reason this shape
was chosen:

- **Uninstalling is deleting a row from configuration.** Turn the entry off and the next
  call goes to Optimizely's procedure again. Nothing has to be restored, because nothing
  was overwritten.
- **A CMS upgrade cannot be broken by us.** The upgrade rewrites the original. It has no
  opinion about a procedure it has never heard of.
- **An upgrade can, however, make our copy stale**, which is the real hazard here. If
  Optimizely changes `netContentDataLoad` in 12.25 and our v1 copy was derived from
  12.24.1, the redirect would quietly serve last version's logic. `originalBodyHash` in
  `config/approved-sql.json` is the guard: it pins the body the copy was derived from, and
  a redirect whose original no longer hashes to that value is skipped, falling back to
  Optimizely's procedure.

## Deployment

These are ordinary DDL scripts and this repository deliberately does not run them. The
shim never executes DDL — it rewrites statements and nothing else — so applying them is a
DBA action, taken deliberately, in a change window, in whatever order your tooling
prefers, except for the dependencies noted below.

Each script is written to be re-runnable: it drops and recreates *its own* versioned
object only.

**Deploy only the scripts for the product versions you run.** Applying all of them is
harmless but pointless: a redirect fires only when the procedure it replaces hashes to the
body that replacement was derived from, so the CMS 11 copies are inert on a CMS 12
database and vice versa. Where two entries cannot be told apart by that hash — Commerce 14
and 15 ship a byte-identical `ecf_CatalogEntry_List` — the configuration discriminates on
the hash of a procedure it *calls*, so those are safe to over-deploy too.

## Contents

### CMS 12

| File | Creates | Replaces the call to |
| --- | --- | --- |
| `netContentDataLoad_optiperf_v1.sql` | `dbo.netContentDataLoad_optiperf_v1` | `dbo.netContentDataLoad` |
| `netContentListPaged_optiperf_v1.sql` | `dbo.netContentListPaged_optiperf_v1` | `dbo.netContentListPaged` |
| `GetListPropertiesOverThreshold_optiperf_v1.sql` | `dbo.GetListPropertiesOverThreshold_optiperf_v1` | *(called by the above; not redirected on its own)* |
| `editDeletePageCheckInternal_optiperf_v1.sql` | `dbo.editDeletePageCheckInternal_optiperf_v1` | `dbo.editDeletePageCheckInternal` |

### CMS 11

| File | Creates | Replaces the call to |
| --- | --- | --- |
| `netContentDataLoad_cms11_optiperf_v1.sql` | `dbo.netContentDataLoad_cms11_optiperf_v1` | `dbo.netContentDataLoad` |
| `netContentListPaged_cms11_optiperf_v1.sql` | `dbo.netContentListPaged_cms11_optiperf_v1` | `dbo.netContentListPaged` *(compatibility level 150+)* |
| `netContentListPaged_cms11_optiperf_v2.sql` | `dbo.netContentListPaged_cms11_optiperf_v2` | `dbo.netContentListPaged` *(compatibility level ≤ 140)* |
| `editDeletePageCheckInternal_cms11_optiperf_v1.sql` | `dbo.editDeletePageCheckInternal_cms11_optiperf_v1` | `dbo.editDeletePageCheckInternal` |

### Either CMS version

| File | Creates | Replaces the call to |
| --- | --- | --- |
| `netContentChildrenReferences_optiperf_v1.sql` | `dbo.netContentChildrenReferences_optiperf_v1` | `dbo.netContentChildrenReferences` |

### Commerce

| File | Creates | Replaces the call to |
| --- | --- | --- |
| `ecf_OrderSearch_optiperf_v1.sql` | `dbo.ecf_OrderSearch_optiperf_v1` | `dbo.ecf_OrderSearch` *(both versions)* |
| `ecf_NodeEntryRelations_optiperf_v1.sql` | `dbo.ecf_NodeEntryRelations_optiperf_v1` | `dbo.ecf_NodeEntryRelations` *(both versions)* |
| `ecf_CatalogEntry_Components_optiperf_v1.sql` | `dbo.ecf_CatalogEntry_Components_optiperf_v1` | `dbo.ecf_CatalogEntry_Components` *(Commerce 15)* |
| `ecf_CatalogEntry_List_optiperf_v1.sql` | `dbo.ecf_CatalogEntry_List_optiperf_v1` | `dbo.ecf_CatalogEntry_List` *(Commerce 15)* |
| `ecf_CatalogEntry_Components_com14_optiperf_v1.sql` | `dbo.ecf_CatalogEntry_Components_com14_optiperf_v1` | `dbo.ecf_CatalogEntry_Components` *(Commerce 14)* |
| `ecf_CatalogEntry_List_com14_optiperf_v1.sql` | `dbo.ecf_CatalogEntry_List_com14_optiperf_v1` | `dbo.ecf_CatalogEntry_List` *(Commerce 14)* |

## Order

Three scripts call another script's object, so deploy the callee first:

- `netContentListPaged_optiperf_v1` → `GetListPropertiesOverThreshold_optiperf_v1`
- `ecf_CatalogEntry_List_optiperf_v1` → `ecf_CatalogEntry_Components_optiperf_v1`
- `ecf_CatalogEntry_List_com14_optiperf_v1` → `ecf_CatalogEntry_Components_com14_optiperf_v1`

Getting the order wrong is inconvenient rather than dangerous. Each of those callers
declares its callee in the `requiredProcedures` precondition, so until the callee exists
the redirect is skipped and Optimizely's procedure keeps running — a half-finished
deployment is inert, not broken.

The pairing itself is deliberate: the callee is *only* reachable from the optimised copy,
so a site running the original procedures never executes it, and turning the redirect off
strands the object harmlessly rather than changing anyone's behaviour.
