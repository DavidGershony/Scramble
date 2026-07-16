use std::path::PathBuf;

use clap::Parser;

use nostr_sdk::RelayUrl;
use whitenoise::cli::config::{Config, KEYRING_SERVICE_ID};
use whitenoise::cli::server;
use whitenoise::{Whitenoise, WhitenoiseConfig};

#[derive(Parser, Debug)]
#[clap(name = "wnd_test", about = "Whitenoise test daemon (mock keyring)")]
struct Args {
    #[clap(long, value_name = "PATH")]
    data_dir: Option<PathBuf>,

    #[clap(long, value_name = "PATH")]
    logs_dir: Option<PathBuf>,
}

#[tokio::main]
async fn main() -> whitenoise::cli::Result<()> {
    let args = Args::parse();
    let config = Config::resolve(args.data_dir.as_ref(), args.logs_dir.as_ref());

    // Use mock keyring so we don't need OS-level keyring in containers
    Whitenoise::initialize_mock_keyring_store();

    let mut wn_config = WhitenoiseConfig::new(&config.data_dir, &config.logs_dir, KEYRING_SERVICE_ID);

    // Add relays: local Docker relay + public test relay for interop testing
    let docker_relay = RelayUrl::parse("ws://nostr-relay:8080")
        .expect("valid relay URL");
    let host_relay = RelayUrl::parse("ws://host.docker.internal:7777")
        .expect("valid relay URL");
    let test_relay = RelayUrl::parse("wss://relay2.angor.io")
        .expect("valid relay URL");
    wn_config = wn_config.with_discovery_relays(vec![docker_relay, host_relay, test_relay]);

    Whitenoise::initialize_whitenoise(wn_config).await?;

    server::run(&config).await
}
