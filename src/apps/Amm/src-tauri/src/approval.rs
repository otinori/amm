// ApprovalBroker (spec: approval-hub, Level 2). Mirrors mcp::McpState's
// WaitBroker pattern: register a pending request, block the calling
// connection until a human answers (or one of the other 3 release
// triggers fires), first-resolution-wins.
use std::collections::HashMap;
use std::sync::atomic::{AtomicU64, Ordering};
use tokio::sync::{oneshot, Mutex};

const DEFAULT_TIMEOUT_MS: u64 = 45_000;

#[derive(Clone, serde::Serialize)]
pub struct ApprovalEntry {
  pub id: String,
  pub token: String,
  #[serde(rename = "toolName")]
  pub tool_name: String,
  #[serde(rename = "toolInput")]
  pub tool_input_json: String,
}

struct Pending {
  entry: ApprovalEntry,
  tx: Option<oneshot::Sender<Option<String>>>,
}

#[derive(Default)]
pub struct ApprovalBroker {
  pending: Mutex<HashMap<String, Pending>>,
  next_id: AtomicU64,
}

impl ApprovalBroker {
  // Registers the request and blocks until resolved by a human decision,
  // explicit release, or the default 45s timeout. Pipe-disconnect release
  // (mcp.rs) works by racing this future via `select!` against a peek-read
  // on the same connection and, on disconnect, calling `release_by_token`
  // *and then* still awaiting this future to completion (never dropping
  // it) - so from this function's own point of view, disconnect release
  // looks exactly like an explicit `resolve()` call. The trailing
  // `pending.remove(&id)` below only matters for the pure-timeout path
  // (nobody ever called `resolve()`); every other path already removed the
  // entry from `resolve()` itself, making this a harmless no-op there.
  pub async fn request(&self, token: &str, tool_name: &str, tool_input_json: &str, timeout_ms: u64) -> Option<String> {
    let id = self.next_id.fetch_add(1, Ordering::Relaxed).to_string();
    let (tx, rx) = oneshot::channel();
    let entry = ApprovalEntry {
      id: id.clone(),
      token: token.to_string(),
      tool_name: tool_name.to_string(),
      tool_input_json: tool_input_json.to_string(),
    };
    self.pending.lock().await.insert(id.clone(), Pending { entry, tx: Some(tx) });

    let decision = match tokio::time::timeout(std::time::Duration::from_millis(timeout_ms), rx).await {
      Ok(Ok(d)) => d,
      _ => None,
    };
    self.pending.lock().await.remove(&id);
    decision
  }

  // Returns false if already resolved (first-wins) or not found. Removes
  // the entry from `pending` immediately rather than leaving that to
  // `request()`'s own post-await cleanup, so a caller that races
  // `request()`'s future via `select!` and ends up dropping it (mcp.rs's
  // pipe-disconnect release path) can't leak an orphaned entry that
  // nothing will ever remove.
  pub async fn resolve(&self, id: &str, decision: Option<String>) -> bool {
    let mut pending = self.pending.lock().await;
    let Some(mut p) = pending.remove(id) else { return false };
    let Some(tx) = p.tx.take() else { return false };
    let _ = tx.send(decision);
    true
  }

  pub async fn release_by_token(&self, token: &str) {
    let ids: Vec<String> = {
      let pending = self.pending.lock().await;
      pending.iter().filter(|(_, p)| p.entry.token == token).map(|(id, _)| id.clone()).collect()
    };
    for id in ids {
      self.resolve(&id, None).await;
    }
  }

  pub async fn release_all(&self) {
    let ids: Vec<String> = self.pending.lock().await.keys().cloned().collect();
    for id in ids {
      self.resolve(&id, None).await;
    }
  }

  pub async fn list(&self) -> Vec<ApprovalEntry> {
    self.pending.lock().await.values().map(|p| p.entry.clone()).collect()
  }
}

pub fn default_timeout_ms() -> u64 {
  DEFAULT_TIMEOUT_MS
}

#[cfg(test)]
mod tests {
  use super::*;

  #[tokio::test]
  async fn resolve_delivers_the_decision_and_removes_the_pending_entry() {
    let broker = ApprovalBroker::default();
    let responder = async {
      loop {
        if let Some(entry) = broker.list().await.into_iter().next() {
          assert!(broker.resolve(&entry.id, Some("allow".to_string())).await);
          break;
        }
        tokio::time::sleep(std::time::Duration::from_millis(5)).await;
      }
    };
    let (decision, _) = tokio::join!(broker.request("tok", "Bash", "{}", 5_000), responder);
    assert_eq!(decision, Some("allow".to_string()));
    assert!(broker.list().await.is_empty());
  }

  #[tokio::test]
  async fn resolve_is_first_wins_and_a_second_call_is_a_no_op() {
    let broker = ApprovalBroker::default();
    broker.pending.lock().await.insert(
      "id1".to_string(),
      Pending {
        entry: ApprovalEntry { id: "id1".to_string(), token: "tok".to_string(), tool_name: "Bash".to_string(), tool_input_json: "{}".to_string() },
        tx: { let (tx, _rx) = oneshot::channel(); Some(tx) },
      },
    );
    assert!(broker.resolve("id1", Some("allow".to_string())).await);
    assert!(!broker.resolve("id1", Some("deny".to_string())).await, "second resolve of the same id must be a no-op");
  }

  #[tokio::test]
  async fn release_by_token_resolves_only_matching_entries_with_none() {
    let broker = ApprovalBroker::default();
    let a = async { broker.request("tok-a", "Bash", "{}", 5_000).await };
    // Short timeout so this test doesn't itself take 5s+ waiting for tok-b's
    // own natural cleanup once tokio::join! below drives it to completion.
    let b = async { broker.request("tok-b", "Bash", "{}", 200).await };
    let releaser = async {
      loop {
        if broker.list().await.len() == 2 {
          break;
        }
        tokio::time::sleep(std::time::Duration::from_millis(5)).await;
      }
      broker.release_by_token("tok-a").await;
      // Snapshot right here: `tokio::join!` below only returns once *every*
      // joined future completes, including tok-b's own 200ms timeout, so
      // asserting against `broker.list()` after the join would see tok-b
      // already self-cleaned-up too and (wrongly) look like both got released.
      broker.list().await
    };
    let (decision_a, decision_b, remaining) = tokio::join!(a, b, releaser);
    assert_eq!(decision_a, None, "the released token's request must resolve to None");
    assert_eq!(remaining.len(), 1, "only the released token's entry should be gone at this point");
    assert_eq!(remaining[0].token, "tok-b");
    assert_eq!(decision_b, None, "tok-b was never released or answered; it resolves via its own short timeout");
  }

  #[tokio::test]
  async fn release_all_resolves_every_pending_entry_with_none() {
    let broker = ApprovalBroker::default();
    let a = async { broker.request("tok-a", "Bash", "{}", 5_000).await };
    let b = async { broker.request("tok-b", "Read", "{}", 5_000).await };
    let releaser = async {
      loop {
        if broker.list().await.len() == 2 {
          broker.release_all().await;
          break;
        }
        tokio::time::sleep(std::time::Duration::from_millis(5)).await;
      }
    };
    let (decision_a, decision_b, _) = tokio::join!(a, b, releaser);
    assert_eq!(decision_a, None);
    assert_eq!(decision_b, None);
    assert!(broker.list().await.is_empty());
  }

  #[tokio::test]
  async fn request_times_out_to_none_when_nobody_answers() {
    let broker = ApprovalBroker::default();
    let decision = broker.request("tok", "Bash", "{}", 20).await;
    assert_eq!(decision, None);
    assert!(broker.list().await.is_empty());
  }
}
