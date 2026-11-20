//! vectors BLOCKS.rlp.hex > VECTORS.jsonl — test vectors for the .NET bindings, which have no signature recovery of
//! their own: per block its RLP, its transactions' senders, and the state root and gas its header carries.

use std::io::{BufRead, Write};

use alloy_consensus::transaction::SignerRecoverable;
use alloy_rlp::Decodable;

type Block = alloy_consensus::Block<reth_ethereum::TransactionSigned>;

fn main() -> eyre::Result<()> {
    let path = std::env::args().nth(1).expect("BLOCKS");
    let stdout = std::io::stdout();
    let mut out = std::io::BufWriter::new(stdout.lock());
    for line in std::io::BufReader::new(std::fs::File::open(path)?).lines() {
        let line = line?;
        let hex = line.trim().trim_start_matches("0x");
        let bytes = alloy_primitives::hex::decode(hex)?;
        let block = Block::decode(&mut &bytes[..])?;
        let senders = block.body.transactions.iter().map(|tx| tx.recover_signer().map(|s| s.to_string())).collect::<Result<Vec<_>, _>>()?;
        let vector = serde_json::json!({
            "number": block.header.number,
            "rlp": format!("0x{hex}"),
            "senders": senders,
            "stateRoot": block.header.state_root.to_string(),
            "gasUsed": block.header.gas_used,
        });
        writeln!(out, "{vector}")?;
    }
    Ok(())
}
