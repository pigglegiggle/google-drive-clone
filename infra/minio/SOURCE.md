# MinIO source

This image builds the upstream MinIO Community source at commit `07c3a429bfed` (release `RELEASE.2025-09-07T16-13-09Z`), rather than downloading an unpublished `minio/minio:latest` image.

Source: https://github.com/minio/minio/tree/07c3a429bfed
License: GNU AGPLv3, https://github.com/minio/minio/blob/07c3a429bfed/LICENSE
Build recipe: this directory's Dockerfile. No MinIO source modifications are applied.

MinIO remains a separate service, accessed over its S3 HTTP API. Its license is separate from the application code. Keep the source reference and license available when distributing the built image. The upstream community repository is archived; this pinned image is for the documented showcase deployment.
