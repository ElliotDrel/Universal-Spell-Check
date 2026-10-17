use crate::{
    channel,
    logging::Logger,
    runtime::{Bus, UiEvent},
    store::Store,
};
use std::{
    path::Path,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
        mpsc::{self, Sender},
    },
    thread,
    time::Duration,
};
use velopack::{UpdateCheck, UpdateInfo, UpdateManager, sources::GithubSource};
#[derive(Clone, Copy, Debug)]
pub enum Trigger {
    Launch,
    Periodic,
    Manual,
    Restart,
}
#[derive(Clone)]
pub struct Updater {
    sender: Sender<Trigger>,
    pub busy: Arc<AtomicBool>,
    restart_queued: Arc<AtomicBool>,
    pub state: Arc<Mutex<String>>,
}
impl Updater {
    pub fn new(store: Store, logger: Logger, bus: Bus) -> Self {
        let (tx, rx) = mpsc::channel();
        let busy = Arc::new(AtomicBool::new(false));
        let restart_queued = Arc::new(AtomicBool::new(false));
        let state = Arc::new(Mutex::new(if channel::IS_DEV {
            "Updates are disabled in Dev".into()
        } else {
            "Standalone build".into()
        }));
        let active = busy.clone();
        let queued = restart_queued.clone();
        let status = state.clone();
        thread::Builder::new().name("updates".into()).spawn(move||{
            let manager=if channel::IS_DEV{None}else{std::panic::catch_unwind(||UpdateManager::new(GithubSource::new("https://github.com/ElliotDrel/Universal-Spell-Check",None,false),None,None)).ok().and_then(Result::ok)};
            let mut pending:Option<Box<UpdateInfo>>=None;
            loop{
                let trigger=match rx.recv_timeout(Duration::from_secs(4*60*60)){Ok(trigger)=>trigger,Err(mpsc::RecvTimeoutError::Timeout)=>Trigger::Periodic,Err(_)=>break};active.store(true,Ordering::Release);
                let result=std::panic::catch_unwind(std::panic::AssertUnwindSafe(||->Result<(),String>{
                    let Some(manager)=manager.as_ref()else{if matches!(trigger,Trigger::Restart){return Err("Restart updates require an installed production app.".into());}return Ok(());};
                    if matches!(trigger,Trigger::Restart){
                        let locator=velopack::locator::auto_locate_app_manifest(velopack::locator::LocationContext::FromCurrentExe).map_err(|e|e.to_string())?;
                        restart_command(&locator.get_update_path(),std::process::id()).spawn().map_err(|e|e.to_string())?;
                        bus.send(UiEvent::ReadyToExit);return Ok(());
                    }
                    *status.lock().unwrap_or_else(|p|p.into_inner())="Checking for updates...".into();bus.send(UiEvent::Update("Checking for updates...".into()));
                    match manager.check_for_updates().map_err(|e|e.to_string())?{
                        UpdateCheck::UpdateAvailable(update)=>{
                            *status.lock().unwrap_or_else(|p|p.into_inner())=format!("Downloading {}",update.TargetFullRelease.Version);
                            manager.download_updates(&update,None).map_err(|e|e.to_string())?;
                            *status.lock().unwrap_or_else(|p|p.into_inner())=format!("Version {} ready — restart to update",update.TargetFullRelease.Version);
                            pending=Some(update);
                            if matches!(trigger,Trigger::Launch){manager.wait_exit_then_apply_updates(&**pending.as_ref().unwrap(),true,true,Vec::<String>::new()).map_err(|e|e.to_string())?;bus.send(UiEvent::ReadyToExit);}
                        }
                        _=>{pending=None;*status.lock().unwrap_or_else(|p|p.into_inner())="Up to date".into();if matches!(trigger,Trigger::Manual){bus.send(UiEvent::Notify("Up to date".into(),format!("You're on the latest version (v{}).",channel::VERSION)));}}
                    }
                    let _=std::fs::write(store.directory.join("last-update-check.txt"),time::OffsetDateTime::now_utc().to_string());Ok(())
                })).unwrap_or_else(|_|Err("The update check stopped after an internal error; the current app remains available.".into()));
                if let Err(error)=result{
                    if matches!(trigger,Trigger::Restart){queued.store(false,Ordering::Release);}
                    logger.event(format!("update_check_failed error={}",serde_json::to_string(&error).unwrap_or_default()));
                    *status.lock().unwrap_or_else(|p|p.into_inner())=pending.as_ref().map(|u|format!("Version {} ready — restart to update",u.TargetFullRelease.Version)).unwrap_or_else(||"Update failed — current version remains available".into());
                    bus.send(UiEvent::Notify("Update failed".into(),error));
                }
                active.store(false,Ordering::Release);bus.send(UiEvent::Update(status.lock().unwrap_or_else(|p|p.into_inner()).clone()));if matches!(trigger,Trigger::Launch){bus.send(UiEvent::StartupChecked);}
            }
        }).expect("update worker");
        Self {
            sender: tx,
            busy,
            restart_queued,
            state,
        }
    }
    pub fn restart_pending(&self) -> bool {
        self.restart_queued.load(Ordering::Acquire)
    }
    pub fn check(&self, trigger: Trigger) -> bool {
        if matches!(trigger, Trigger::Restart) {
            if self.restart_queued.swap(true, Ordering::AcqRel) {
                return true;
            }
            if self.sender.send(trigger).is_err() {
                self.restart_queued.store(false, Ordering::Release);
                return false;
            }
            return true;
        }
        if self.busy.swap(true, Ordering::AcqRel) {
            return false;
        }
        if self.sender.send(trigger).is_err() {
            self.busy.store(false, Ordering::Release);
            return false;
        }
        true
    }
}
fn restart_command(path: &Path, pid: u32) -> std::process::Command {
    use std::os::windows::process::CommandExt;
    let mut command = std::process::Command::new(path);
    command
        .arg("start")
        .arg("--waitPid")
        .arg(pid.to_string())
        .creation_flags(0x08000000);
    if let Some(parent) = path.parent() {
        command.current_dir(parent);
    }
    command
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn restart_is_queued_during_check_and_deduplicated() {
        let (tx, rx) = mpsc::channel();
        let updater = Updater {
            sender: tx,
            busy: Arc::new(AtomicBool::new(true)),
            restart_queued: Arc::new(AtomicBool::new(false)),
            state: Arc::new(Mutex::new(String::new())),
        };
        assert!(updater.check(Trigger::Restart));
        assert!(updater.check(Trigger::Restart));
        assert!(matches!(rx.try_recv(), Ok(Trigger::Restart)));
        assert!(rx.try_recv().is_err());
        assert!(!updater.check(Trigger::Manual));
    }
    #[test]
    fn graceful_restart_uses_existing_updater_wire_arguments() {
        let command = restart_command(Path::new("C:/app/Update.exe"), 123);
        assert_eq!(
            command
                .get_args()
                .map(|a| a.to_string_lossy().into_owned())
                .collect::<Vec<_>>(),
            ["start", "--waitPid", "123"]
        );
        assert_eq!(command.get_current_dir(), Some(Path::new("C:/app")));
    }
}
