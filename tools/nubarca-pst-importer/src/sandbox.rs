//! Self-confinement, applied once per invocation before the first byte of the
//! PST is parsed. The PST is untrusted input; whatever a parser bug could be
//! made to do, the process that runs it can only:
//!
//! * read the PST it was given and write inside its work/out directory
//!   (Landlock, when the kernel has it);
//! * make no network connection and start no other program (seccomp);
//! * run as an unprivileged user (when started as root with `--run-as`, it
//!   switches uid/gid before parsing), hold no capability and never gain
//!   privileges (capability bounding set emptied, `no_new_privs`);
//! * stay within memory, CPU-time, file-size and descriptor limits (rlimits).
//!
//! The parent (NubArca's worker) also starts it with an EMPTY environment, so
//! nothing of the API's configuration is inherited. Every layer is reported by
//! `probe` and checked end to end by `sandbox-check`.

use std::io;
use std::path::{Path, PathBuf};

use serde::Serialize;

/// What the invocation may touch, and how strict to be.
#[derive(Clone, Debug)]
pub struct Policy {
    pub read_files: Vec<PathBuf>,
    pub write_dir: Option<PathBuf>,
    pub max_memory_bytes: u64,
    pub max_cpu_seconds: u64,
    pub max_file_bytes: u64,
    pub require_landlock: bool,
    pub run_as: Option<(u32, u32)>,
}

#[derive(Clone, Debug, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Report {
    pub rlimits: bool,
    /// `switched` (root → `--run-as`), `unprivileged` (started non-root) or
    /// `root` (started as root without `--run-as`).
    pub user: &'static str,
    pub no_new_privs: bool,
    /// `dropped` (bounding set emptied), `none_held` (unprivileged already).
    pub capabilities: &'static str,
    pub seccomp: bool,
    /// `full`, `partial` (older kernel ABI) or `unavailable`.
    pub landlock: &'static str,
}

#[derive(Debug)]
pub struct SandboxError(pub String);

/// Applies every layer. A failure of rlimits, `no_new_privs` or seccomp is
/// fatal (they work on every supported Linux); Landlock is fatal only with
/// `require_landlock`, otherwise reported as `unavailable`.
pub fn enter(policy: &Policy) -> Result<Report, SandboxError> {
    let mut report = Report {
        user: "unprivileged",
        capabilities: "none_held",
        landlock: "unavailable",
        ..Report::default()
    };
    imp::rlimits(policy).map_err(|e| SandboxError(format!("rlimits: {e}")))?;
    report.rlimits = true;
    // Bounding set first: emptying it needs CAP_SETPCAP, which the uid
    // switch gives up; the switch itself needs CAP_SETUID/SETGID/CHOWN.
    let held = imp::drop_bounding_set();
    report.user = imp::switch_user(policy).map_err(|e| SandboxError(format!("user: {e}")))?;
    imp::clear_capabilities();
    report.capabilities = if held { "dropped" } else { "none_held" };
    imp::no_new_privs().map_err(|e| SandboxError(format!("no_new_privs: {e}")))?;
    report.no_new_privs = true;
    report.landlock = imp::landlock(policy);
    if policy.require_landlock && report.landlock == "unavailable" {
        return Err(SandboxError("landlock unavailable".into()));
    }
    // Last: seccomp also forbids the syscalls the steps above needed.
    imp::seccomp().map_err(|e| SandboxError(format!("seccomp: {e}")))?;
    report.seccomp = true;
    Ok(report)
}

/// Result of one forbidden action attempted inside the sandbox.
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Checks {
    pub network: &'static str,
    pub exec: &'static str,
    pub read_outside: &'static str,
    pub write_outside: &'static str,
    pub write_inside: &'static str,
    /// Names only, never values: proves what the parent passed down.
    pub environment: Vec<String>,
}

/// Attempts what the sandbox must forbid (and the one thing it must allow).
/// `outside_read` is a readable file outside the allowed set; `outside_dir`
/// a writable directory outside it.
pub fn check(write_dir: &Path, outside_read: &Path, outside_dir: &Path) -> Checks {
    fn verdict<T>(r: io::Result<T>) -> &'static str {
        match r {
            Ok(_) => "allowed",
            Err(_) => "denied",
        }
    }
    let inside = write_dir.join("sandbox-check.tmp");
    let write_inside = verdict(std::fs::write(&inside, b"ok"));
    let _ = std::fs::remove_file(&inside);
    let outside = outside_dir.join(format!("nubarca-sandbox-check-{}.tmp", std::process::id()));
    let write_outside = verdict(std::fs::write(&outside, b"x"));
    let _ = std::fs::remove_file(&outside);
    Checks {
        network: verdict(imp::try_socket()),
        exec: verdict(
            std::process::Command::new("/bin/sh")
                .arg("-c")
                .arg("true")
                .status(),
        ),
        read_outside: verdict(std::fs::read(outside_read)),
        write_outside,
        write_inside,
        environment: std::env::vars_os()
            .map(|(k, _)| k.to_string_lossy().into_owned())
            .collect(),
    }
}

#[cfg(target_os = "linux")]
mod imp {
    use super::Policy;
    use std::collections::BTreeMap;
    use std::io;

    fn check(rc: libc::c_int) -> io::Result<()> {
        if rc == 0 {
            Ok(())
        } else {
            Err(io::Error::last_os_error())
        }
    }

    fn set_limit(resource: libc::__rlimit_resource_t, value: u64) -> io::Result<()> {
        let mut current = libc::rlimit {
            rlim_cur: 0,
            rlim_max: 0,
        };
        // SAFETY: plain libc calls on a stack value.
        check(unsafe { libc::getrlimit(resource, &mut current) })?;
        // Never raise a limit the parent already set lower.
        let value = value.min(current.rlim_max);
        let limit = libc::rlimit {
            rlim_cur: value,
            rlim_max: value,
        };
        check(unsafe { libc::setrlimit(resource, &limit) })
    }

    pub fn rlimits(policy: &Policy) -> io::Result<()> {
        set_limit(libc::RLIMIT_CORE, 0)?;
        set_limit(libc::RLIMIT_NOFILE, 64)?;
        if policy.max_memory_bytes > 0 {
            set_limit(libc::RLIMIT_AS, policy.max_memory_bytes)?;
        }
        if policy.max_cpu_seconds > 0 {
            set_limit(libc::RLIMIT_CPU, policy.max_cpu_seconds)?;
        }
        if policy.max_file_bytes > 0 {
            // A write past the limit then fails with EFBIG (an io_error for
            // that item) instead of killing the process with SIGXFSZ.
            // SAFETY: setting a signal disposition to SIG_IGN.
            unsafe { libc::signal(libc::SIGXFSZ, libc::SIG_IGN) };
            set_limit(libc::RLIMIT_FSIZE, policy.max_file_bytes)?;
        }
        Ok(())
    }

    pub fn no_new_privs() -> io::Result<()> {
        // SAFETY: prctl with integer arguments.
        check(unsafe { libc::prctl(libc::PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) })
    }

    #[repr(C)]
    struct CapHeader {
        version: u32,
        pid: libc::c_int,
    }

    #[repr(C)]
    #[derive(Clone, Copy)]
    struct CapData {
        effective: u32,
        permitted: u32,
        inheritable: u32,
    }

    const CAP_VERSION_3: u32 = 0x2008_0522;

    // Empties the bounding and ambient sets (needs CAP_SETPCAP, so before
    // the uid switch); returns whether any capability was held.
    pub fn drop_bounding_set() -> bool {
        let mut header = CapHeader {
            version: CAP_VERSION_3,
            pid: 0,
        };
        let mut data = [CapData {
            effective: 0,
            permitted: 0,
            inheritable: 0,
        }; 2];
        // SAFETY: capget with correctly sized v3 structures.
        let held = unsafe { libc::syscall(libc::SYS_capget, &mut header, data.as_mut_ptr()) } == 0
            && data.iter().any(|d| d.permitted != 0 || d.effective != 0);
        for cap in 0..64 {
            unsafe { libc::prctl(libc::PR_CAPBSET_DROP, cap, 0, 0, 0) };
        }
        unsafe {
            libc::prctl(
                libc::PR_CAP_AMBIENT,
                libc::PR_CAP_AMBIENT_CLEAR_ALL as libc::c_ulong,
                0,
                0,
                0,
            )
        };
        held
    }

    // Then every set of this process (already empty after a uid switch).
    pub fn clear_capabilities() {
        let mut header = CapHeader {
            version: CAP_VERSION_3,
            pid: 0,
        };
        let zero = [CapData {
            effective: 0,
            permitted: 0,
            inheritable: 0,
        }; 2];
        // SAFETY: capset with correctly sized v3 structures.
        unsafe { libc::syscall(libc::SYS_capset, &mut header, zero.as_ptr()) };
    }

    // Root never parses the PST. With `--run-as`, the work directory is
    // handed to that user, then groups, gid and uid are switched for good
    // (setres*: real, effective and saved), which also clears every
    // capability. The new user must still reach the PST and the directory;
    // if it cannot (a parent directory closed to others), the invocation
    // fails here instead of half-way through an import.
    pub fn switch_user(policy: &Policy) -> io::Result<&'static str> {
        use std::ffi::CString;
        use std::os::unix::ffi::OsStrExt;
        // SAFETY: plain libc identity calls.
        if unsafe { libc::geteuid() } != 0 {
            return Ok("unprivileged");
        }
        let Some((uid, gid)) = policy.run_as else {
            return Ok("root");
        };
        let c = |p: &std::path::Path| {
            CString::new(p.as_os_str().as_bytes())
                .map_err(|_| io::Error::new(io::ErrorKind::InvalidInput, "path"))
        };
        if let Some(dir) = &policy.write_dir {
            check(unsafe { libc::chown(c(dir)?.as_ptr(), uid, gid) })?;
        }
        check(unsafe { libc::setgroups(0, std::ptr::null()) })?;
        check(unsafe { libc::setresgid(gid, gid, gid) })?;
        check(unsafe { libc::setresuid(uid, uid, uid) })?;
        if unsafe { libc::getuid() } != uid || unsafe { libc::setuid(0) } == 0 {
            return Err(io::Error::other("uid switch did not hold"));
        }
        for file in &policy.read_files {
            if unsafe { libc::access(c(file)?.as_ptr(), libc::R_OK) } != 0 {
                return Err(io::Error::other(
                    "the unprivileged user cannot read the PST copy",
                ));
            }
        }
        if let Some(dir) = &policy.write_dir {
            if unsafe { libc::access(c(dir)?.as_ptr(), libc::W_OK | libc::X_OK) } != 0 {
                return Err(io::Error::other(
                    "the unprivileged user cannot write the work directory",
                ));
            }
        }
        Ok("switched")
    }

    pub fn landlock(policy: &Policy) -> &'static str {
        use landlock::{
            Access, AccessFs, PathBeneath, PathFd, Ruleset, RulesetAttr, RulesetCreatedAttr,
            RulesetStatus, ABI,
        };
        let abi = ABI::V3;
        let all = AccessFs::from_all(abi);
        let result = (|| -> Result<RulesetStatus, Box<dyn std::error::Error>> {
            let mut ruleset = Ruleset::default().handle_access(all)?.create()?;
            for file in &policy.read_files {
                // A file rule may only carry file rights.
                let fd = PathFd::new(file)?;
                ruleset = ruleset.add_rule(PathBeneath::new(fd, AccessFs::ReadFile))?;
            }
            if let Some(dir) = &policy.write_dir {
                let fd = PathFd::new(dir)?;
                ruleset = ruleset.add_rule(PathBeneath::new(fd, all))?;
            }
            Ok(ruleset.restrict_self()?.ruleset)
        })();
        match result {
            Ok(RulesetStatus::FullyEnforced) => "full",
            Ok(RulesetStatus::PartiallyEnforced) => "partial",
            Ok(RulesetStatus::NotEnforced) | Err(_) => "unavailable",
        }
    }

    pub fn seccomp() -> Result<(), String> {
        use seccompiler::{BpfProgram, SeccompAction, SeccompFilter, TargetArch};
        let arch: TargetArch = std::env::consts::ARCH
            .try_into()
            .map_err(|_| "unsupported architecture".to_string())?;
        let mut denied: Vec<libc::c_long> = vec![
            // network
            libc::SYS_socket,
            libc::SYS_socketpair,
            libc::SYS_connect,
            libc::SYS_bind,
            libc::SYS_listen,
            libc::SYS_accept,
            libc::SYS_accept4,
            // new programs and processes (the engine is single-threaded)
            libc::SYS_execve,
            libc::SYS_execveat,
            libc::SYS_clone,
            libc::SYS_clone3,
            // reaching other processes or the kernel
            libc::SYS_ptrace,
            libc::SYS_process_vm_readv,
            libc::SYS_process_vm_writev,
            libc::SYS_mount,
            libc::SYS_umount2,
            libc::SYS_pivot_root,
            libc::SYS_chroot,
            libc::SYS_unshare,
            libc::SYS_setns,
            libc::SYS_init_module,
            libc::SYS_finit_module,
            libc::SYS_delete_module,
            libc::SYS_kexec_load,
            libc::SYS_bpf,
            libc::SYS_perf_event_open,
            libc::SYS_keyctl,
            libc::SYS_add_key,
            libc::SYS_request_key,
            libc::SYS_userfaultfd,
            libc::SYS_io_uring_setup,
        ];
        #[cfg(target_arch = "x86_64")]
        denied.extend([libc::SYS_fork, libc::SYS_vfork]);
        let rules: BTreeMap<i64, Vec<seccompiler::SeccompRule>> =
            denied.into_iter().map(|nr| (nr, vec![])).collect();
        let filter = SeccompFilter::new(
            rules,
            SeccompAction::Allow,
            SeccompAction::Errno(libc::EPERM as u32),
            arch,
        )
        .map_err(|e| e.to_string())?;
        let program: BpfProgram = filter
            .try_into()
            .map_err(|e: seccompiler::BackendError| e.to_string())?;
        seccompiler::apply_filter(&program).map_err(|e| e.to_string())
    }

    pub fn try_socket() -> io::Result<()> {
        // SAFETY: socket(2) with constant arguments; closed if it succeeds.
        let fd = unsafe { libc::socket(libc::AF_INET, libc::SOCK_STREAM, 0) };
        if fd < 0 {
            return Err(io::Error::last_os_error());
        }
        unsafe { libc::close(fd) };
        Ok(())
    }
}

#[cfg(not(target_os = "linux"))]
mod imp {
    //! Development builds on other systems run unconfined; the product only
    //! ships the Linux binary.
    use super::Policy;
    use std::io;

    pub fn rlimits(_: &Policy) -> io::Result<()> {
        Ok(())
    }
    pub fn no_new_privs() -> io::Result<()> {
        Ok(())
    }
    pub fn drop_bounding_set() -> bool {
        false
    }
    pub fn clear_capabilities() {}
    pub fn switch_user(_: &Policy) -> io::Result<&'static str> {
        Ok("unprivileged")
    }
    pub fn landlock(_: &Policy) -> &'static str {
        "unavailable"
    }
    pub fn seccomp() -> Result<(), String> {
        Ok(())
    }
    pub fn try_socket() -> io::Result<()> {
        std::net::TcpListener::bind("127.0.0.1:0").map(|_| ())
    }
}
