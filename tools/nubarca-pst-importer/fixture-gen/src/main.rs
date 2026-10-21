//! Regenerate fixtures:
//!
//! ```text
//! cargo run -p nubarca-pst-fixture-gen -- basic  tests/fixtures/basic.pst
//! cargo run -p nubarca-pst-fixture-gen -- large  /tmp/large.pst 134217728
//! cargo run -p nubarca-pst-fixture-gen -- volume /tmp/volume.pst 20000
//! ```

use std::path::PathBuf;
use std::process::ExitCode;

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let result = match args.as_slice() {
        [kind, out] if kind == "basic" => nubarca_pst_fixture_gen::write_basic(&PathBuf::from(out)),
        [kind, out, size] if kind == "large" => match size.parse() {
            Ok(size) => nubarca_pst_fixture_gen::write_large_attachment(&PathBuf::from(out), size)
                .map(|sha| println!("sha256 {sha}")),
            Err(_) => Err(std::io::Error::other("size must be an integer")),
        },
        [kind, out, n] if kind == "volume" => match n.parse() {
            Ok(n) => nubarca_pst_fixture_gen::write_volume(&PathBuf::from(out), n, 40, 10),
            Err(_) => Err(std::io::Error::other("count must be an integer")),
        },
        _ => {
            eprintln!(
                "usage: nubarca-pst-fixture-gen basic OUT | large OUT BYTES | volume OUT COUNT"
            );
            return ExitCode::from(3);
        }
    };
    match result {
        Ok(()) => ExitCode::SUCCESS,
        Err(e) => {
            eprintln!("fixture generation failed: {e}");
            ExitCode::FAILURE
        }
    }
}
