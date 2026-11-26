# Private routine video library

The backend exposes all five operations in the [approved design](superpowers/specs/2026-11-23-routine-video-library-design.md). Videos belong to a gym, not an individual routine. Only enabled Trainers may write; Trainers and Students with current persisted coverage may list and play. GymAdmin and global SuperAdmin gain no access.

## HTTP contract

| Method and route | Input | Success |
| --- | --- | --- |
| `GET /api/routines/videos` | Optional `page`, `pageSize`, `category` | `200`, safe catalog DTOs |
| `POST /api/routines/videos` | Multipart `title`, `category`, optional `description`, one `file` | `201` with content Location |
| `GET /api/routines/videos/{id}/content` | Optional single `Range: bytes=...` | `200` or `206`, streamed MP4 |
| `PUT /api/routines/videos/{id}/content?expectedRevision={revision}` | Multipart one `file` | `200`, updated catalog entry |
| `DELETE /api/routines/videos/{id}?expectedRevision={revision}` | No body | `204` after catalog/cleanup commit |

Writes require the existing authentication cookie and `X-CSRF-TOKEN`. Replacement keeps ID and metadata and rotates revision. Missing/foreign IDs return `404`; stale revisions return `409`. The file limit is 100,000,000 bytes (`413` above the limit). Invalid files return `400`. Missing revisions bind to an empty GUID and cannot match a real catalog revision.

Playback is no-store, never exposes private keys/URLs, and checks live authorization for each request. Inclusive, open-ended, and suffix byte ranges are supported. Oversized ends are clamped; invalid, overflowing, multi-range, and unsatisfiable requests return `416` with `Content-Range: bytes */{length}`. The storage stream is already sliced; MVC range processing is deliberately disabled to avoid seeking twice. An already-open playback may race physical deletion; delivered bytes cannot be recalled.

## Validation boundaries

API validates MP4 ISO BMFF box lengths, compatible brands, a movie/video-track handler and nonempty media data, independent of filename/MIME. Validation is bounded and cancellation-aware and rewinds the buffered upload before storage. It rejects audio-only containers, brand-only headers and truncated boxes. It is **not a decoder, malware scanner, or guarantee of playable codecs/sample tables**. Transcoding and browser/device compatibility checks remain outside this implementation.

## Storage and deployment

The existing adapter uses cached `AWSSDK.S3` 4.1.2.1, not bespoke signing. Configure these settings externally:

- `VideoStorage__BucketName`: explicit private standard AWS bucket (no dotted/directory buckets).
- `VideoStorage__Region`: SDK-recognized AWS region.
- `VideoStorage__AccessKeyId`, `VideoStorage__SecretAccessKey`: credentials from a secret provider.
- `VideoStorage__CleanupEnabled=true`: enable the hosted cleanup worker **only after** operator-reviewed migration and storage provisioning. Default is disabled; durable work remains queued while disabled.

No startup storage probe, ambient credentials, endpoint override, public URL or live bucket provisioning is performed. Immutable PUTs use `If-None-Match: *`. GET validates the storage status, byte interval and content length and transfers SDK response disposal to the stream. SDK retries are disabled; external errors are not treated as success. Session-token credentials are not supported by the current options.

The prepared `20261125013528_AddVideoLibrary` migration creates only catalog/object-state tables and restrictive keys/indexes. Offline migration tests verify model compatibility, SQL generation and rollback guards. **No database migration has been applied.** Rollback refuses nonempty catalog or object-state tables, including cleanup tombstones; it is not a storage-deletion tool.

## Durable cleanup and races

DAL persists a pending key before external PUT. Publication acquires the existing transaction-owned gym mutex, checks the enabled same-gym Trainer and pending expiry/state, then atomically switches the catalog reference and schedules the old key. A losing revision never changes the winner. Failed/abandoned uploads remain pending until expiry.

Cleanup claims only due, nonactive, unreferenced keys under the same gym mutex and commits a `Cleanup` tombstone **before** external deletion. Publication can never activate that tombstone. A five-minute retry lease survives failures/restarts. Successful deletions retain tombstones and retry daily: a delayed PUT completing after an earlier DELETE is eventually removed. Tombstones deliberately grow until a separately designed safe retention policy exists; do not prune them based only on an earlier successful DELETE. Duplicate workers may delete the same immutable retired key idempotently but cannot reactivate it.

The worker uses fresh scopes, batches up to 20 attempts, and waits 30 seconds between batches with shutdown cancellation. Cleanup failure does not undo catalog deletion. Periodic cleanup must remain enabled operationally to achieve eventual physical deletion.

## Local evidence and limits

Tests use in-process HTTP, SQLite, and fully substituted SDK/storage operations; no live S3 or application database is contacted. Regression cases exercise HTTP lifecycle, authorization/coverage, exact byte intervals, failed storage, stale competing publication, retry leases, tombstone-before-delete and late-PUT cleanup. SQL Server migration shape is checked offline. SQLite tests exercise controlled interleavings, not simultaneous production SQL Server locks. Real AWS interoperability, production SQL Server contention, codec decoding, and reverse-proxy request-size behavior still need deployment validation.
