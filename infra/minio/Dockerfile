FROM golang:1.24.7-bookworm AS build
ARG MINIO_COMMIT=07c3a429bfed433e49018cb0f78a52145d4bedeb
RUN git clone --filter=blob:none https://github.com/minio/minio.git /src \
    && cd /src && git checkout "$MINIO_COMMIT"
WORKDIR /src
RUN CGO_ENABLED=0 go build -trimpath -ldflags='-s -w' -o /out/minio .

FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --gid 10001 minio && useradd --uid 10001 --gid minio minio \
    && mkdir /data && chown minio:minio /data
COPY --from=build /out/minio /usr/local/bin/minio
COPY SOURCE.md /usr/share/doc/minio/SOURCE.md
USER 10001:10001
EXPOSE 9000 9001
ENTRYPOINT ["minio"]
CMD ["server", "/data", "--address", ":9000", "--console-address", ":9001"]
