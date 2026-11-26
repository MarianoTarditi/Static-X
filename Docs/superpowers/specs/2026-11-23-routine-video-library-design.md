# Routine video library: share and manage gym videos

The video library is a separate section within Routines. It contains only uploaded informational videos and exercise demonstrations. Videos belong to a gym, not to an individual routine, exercise, or student profile. This design covers the backend only.

## Access and content

- Enabled trainers can list, play, upload, replace, and delete videos in their own gym. Students can list and play videos only in their own gym and only while their existing live coverage rule allows access. Other roles receive no new permissions.
- Each entry requires a title and one category (`informational` or `exercise`), with an optional description. Uploads accept one MP4 file, up to 100 MB (100,000,000 bytes). Validate the byte limit and video format before publication; do not rely on the supplied filename or MIME type. Images, documents, and external links are not accepted.
- SQL Server stores the catalog, including gym ownership, a stable video ID, an opaque revision and a private object reference. S3 stores the bytes. The API streams content and supports byte ranges for seeking; it rechecks authorization for every playback or range request. Do not expose object keys or direct S3 URLs to clients.

## HTTP contract

| Endpoint | Result |
| --- | --- |
| `POST /api/routines/videos` | Trainer uploads a file and metadata. Returns `201` with its catalog entry once ready to play. |
| `GET /api/routines/videos` | Trainer or eligible student lists the gym's available entries, paginated and optionally filtered by category. |
| `GET /api/routines/videos/{videoId}/content` | Trainer or eligible student plays the current file. Returns `200` for a full response or `206` for a satisfiable byte range. |
| `PUT /api/routines/videos/{videoId}/content` | Trainer replaces the file and receives the updated entry. Keeps the same ID, title, category, and description. |
| `DELETE /api/routines/videos/{videoId}` | Trainer removes the entry from the catalog and blocks new playback requests. Returns `204` after the catalog change and durable cleanup request commit. |

Reuse existing authentication and CSRF enforcement for writes. Derive the gym from server-side identity; missing and out-of-gym IDs both return `404`. Invalid files are rejected without publication, oversized uploads return `413`, and unsatisfiable byte ranges return `416`. Replace and delete require the revision obtained from the catalog; stale revisions return `409` without changing the current entry.

## Replacement and deletion

- **Replace:** Validate and store the new file under a fresh private key. Switch the catalog reference only after storage succeeds and the revision still matches. An unsuccessful replacement leaves the existing file playable. A concurrent stale request must not remove or overwrite the winner's file.
- **Delete:** Remove the entry and enqueue cleanup of its object in one SQL transaction. New catalog and playback requests cannot see the entry after commit, even if S3 deletion must be retried. Bytes already delivered cannot be recalled.
- **Cleanup:** Record pending uploads durably before writing their objects. Commit the new catalog reference and cleanup of any superseded key together. Retry cleanup idempotently for superseded, deleted, or abandoned files; never delete an object still referenced by an active entry or upload. No previous version is exposed to the user.

## Layer ownership and acceptance

API owns uploads, range responses, and the S3 adapter. BLL owns catalog DTOs, authorization and publication rules. DAL owns gym-scoped catalog queries, revision-checked writes and durable cleanup state. Entity holds catalog models without HTTP, EF Core, or S3 dependencies. S3 settings and credentials stay external; the adapter can be replaced in isolated tests.

- [ ] Only valid in-limit MP4 uploads become visible. Students cannot upload, replace, or delete.
- [ ] Catalog and playback isolate gyms and enforce current student coverage, including range requests.
- [ ] A successful replacement keeps the catalog ID and serves the new bytes. Failed uploads and stale or competing replacements do not disrupt the current video.
- [ ] Deletion hides the entry immediately from new requests, even if physical cleanup is delayed. Cleanup retries do not delete current objects.

Out of scope: frontend, per-student assignment, links from exercises, transcoding, and configuration of a live bucket or application of a database migration during local implementation.

## Current integration status

The bounded HTTP integration exposes **only `GET /api/routines/videos`**. It uses the existing BLL service, response mapping and gym-scoped DAL repository. The gym and role come from authenticated claims, never query parameters. Trainer access and the default live Student coverage middleware remain in force. The catalog accepts `page` (1–100000, default 1), `pageSize` (1–100, default 20) and optional `category` (`informational` or `exercise`), returning `{ "items": [...] }` with only ID, revision, title, category, description and length.

Upload, playback, replacement and deletion HTTP endpoints and the cleanup worker are **not composed in this unit**. Existing BLL/DAL operations do not imply that these routes are available. The acceptance checklist above describes the full design, not completed HTTP functionality.

### External storage configuration

API composition registers the real `S3VideoObjectStore` adapter using `AWSSDK.S3`. The catalog never calls storage. Storage validation and SDK client creation occur only on an actual storage operation, so missing storage configuration does not prevent host startup or catalog reads.

| External setting | Requirement |
| --- | --- |
| `VideoStorage__BucketName` | Explicit private standard AWS S3 bucket; 3–63 lowercase letters, digits or hyphens, starting and ending with a letter or digit. Directory buckets and dotted names are not supported by this adapter. |
| `VideoStorage__Region` | Explicit region recognized by the installed SDK. |
| `VideoStorage__AccessKeyId` | Explicit access key from an external secret provider. |
| `VideoStorage__SecretAccessKey` | Explicit secret key from an external secret provider. |

No bucket, credentials or custom endpoint are inferred from the SDK's ambient configuration. The adapter uses HTTPS AWS regional endpoints and ignores configured SDK endpoint URL overrides. It performs no startup probe. Object writes use `If-None-Match: *` to prevent overwriting an existing key; reads request and validate the exact byte interval and transfer response ownership to the returned stream. Delete uses S3's idempotent object deletion operation. Storage errors and cancellation are not converted to successful outcomes. SDK automatic retries are disabled; durable cleanup retries remain the repository's responsibility when a separately authorized worker is introduced.

Operators must separately provision a private bucket with public access blocked, restrictive IAM permissions for the required object operations, and suitable encryption/lifecycle settings. This work does not provision or contact a bucket, configure credentials, apply migrations, or enable background video activity. Adapter tests replace SDK operations; HTTP tests use only the in-process host and local SQLite persistence. Live S3 interoperability remains unverified.
