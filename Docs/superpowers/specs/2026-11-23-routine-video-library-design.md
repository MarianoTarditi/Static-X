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
