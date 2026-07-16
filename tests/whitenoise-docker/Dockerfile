# Multi-stage build for whitenoise-rs CLI + daemon
# Stage 1: Build from source + custom test binary with mock keyring
FROM rust:1.90-bookworm AS builder

RUN apt-get update && apt-get install -y \
    pkg-config \
    libssl-dev \
    libsqlite3-dev \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /build
# Pull the latest whitenoise-rs from the default branch (master) for newest updates.
# WHITENOISE_REF can be overridden at build time; default is the master branch HEAD.
ARG WHITENOISE_REF=master
ARG CACHEBUST=2
RUN echo "Building whitenoise-rs ref=${WHITENOISE_REF} cachebust=${CACHEBUST}" \
    && git clone https://github.com/marmot-protocol/whitenoise-rs.git . \
    && git checkout ${WHITENOISE_REF} \
    && git log -1 --oneline

# Patch Relay::defaults() so release builds use our docker-network test relays
# instead of the public mainnet defaults (relay.damus.io / primal / nos.lol).
# Without this, group_relays in WN-issued Welcomes point at public relays that
# Scramble peers cannot publish to (rate limits) and that WN itself does not
# subscribe to for kind 445 group messages, breaking interop with the local
# nostr-relay used by the test suite.
RUN sed -i \
    -e 's|"wss://relay.damus.io"|"ws://nostr-relay:8080"|g' \
    -e 's|"wss://relay.primal.net"|"ws://host.docker.internal:7777"|g' \
    -e 's|"wss://nos.lol"|"wss://relay2.angor.io"|g' \
    src/whitenoise/relays.rs \
    && grep -n 'nostr-relay\|host.docker.internal\|thedude.cloud' src/whitenoise/relays.rs

# Build the standard CLI binaries
RUN cargo build --release --features cli

# Build a custom test daemon that uses mock keyring (no OS keyring needed)
COPY wnd_test.rs /build/src/bin/wnd_test.rs
RUN echo '\n[[bin]]\nname = "wnd_test"\npath = "src/bin/wnd_test.rs"\nrequired-features = ["cli"]' >> Cargo.toml
RUN cargo build --release --features cli,integration-tests --bin wnd_test

# Stage 2: Runtime image
FROM debian:bookworm-slim

RUN apt-get update && apt-get install -y \
    ca-certificates \
    libssl3 \
    libsqlite3-0 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=builder /build/target/release/wnd /usr/local/bin/wnd
COPY --from=builder /build/target/release/wnd_test /usr/local/bin/wnd_test
COPY --from=builder /build/target/release/wn /usr/local/bin/wn

RUN mkdir -p /data /logs

ENV RUST_LOG=info,whitenoise=debug,mdk_core=debug

# Use the test daemon with mock keyring (no OS keyring needed in containers)
ENTRYPOINT ["wnd_test", "--data-dir", "/data", "--logs-dir", "/logs"]
