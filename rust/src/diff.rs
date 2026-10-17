#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Kind {
    Equal,
    Delete,
    Insert,
}
#[derive(Clone, Debug)]
pub struct Edit {
    pub kind: Kind,
    pub text: Vec<u16>,
}
fn push(out: &mut Vec<Edit>, kind: Kind, text: &[u16]) {
    if text.is_empty() {
        return;
    }
    if let Some(last) = out.last_mut().filter(|e| e.kind == kind) {
        last.text.extend_from_slice(text);
    } else {
        out.push(Edit {
            kind,
            text: text.to_vec(),
        });
    }
}
pub fn diff(source: &[u16], target: &[u16]) -> Vec<Edit> {
    let mut prefix = 0;
    let mut suffix = 0;
    if source.len().saturating_mul(target.len()) > 1_000_000 {
        while prefix < source.len().min(target.len()) && source[prefix] == target[prefix] {
            prefix += 1;
        }
        while suffix < source.len().min(target.len()) - prefix
            && source[source.len() - 1 - suffix] == target[target.len() - 1 - suffix]
        {
            suffix += 1;
        }
    }
    let a = &source[prefix..source.len() - suffix];
    let b = &target[prefix..target.len() - suffix];
    let mut out = Vec::new();
    push(&mut out, Kind::Equal, &source[..prefix]);
    if a == b {
        push(&mut out, Kind::Equal, a);
    } else if a.len().saturating_mul(b.len()) > 1_000_000 {
        push(&mut out, Kind::Delete, a);
        push(&mut out, Kind::Insert, b);
    } else {
        let width = b.len() + 1;
        let mut lcs = vec![0u32; (a.len() + 1) * width];
        for i in 1..=a.len() {
            for j in 1..=b.len() {
                lcs[i * width + j] = if a[i - 1] == b[j - 1] {
                    lcs[(i - 1) * width + j - 1] + 1
                } else {
                    lcs[(i - 1) * width + j].max(lcs[i * width + j - 1])
                };
            }
        }
        let (mut i, mut j) = (a.len(), b.len());
        let mut raw = Vec::new();
        while i > 0 || j > 0 {
            if i > 0 && j > 0 && a[i - 1] == b[j - 1] {
                raw.push((Kind::Equal, a[i - 1]));
                i -= 1;
                j -= 1;
            } else if j > 0 && (i == 0 || lcs[i * width + j - 1] >= lcs[(i - 1) * width + j]) {
                raw.push((Kind::Insert, b[j - 1]));
                j -= 1;
            } else {
                raw.push((Kind::Delete, a[i - 1]));
                i -= 1;
            }
        }
        for (k, c) in raw.into_iter().rev() {
            push(&mut out, k, &[c]);
        }
    }
    push(&mut out, Kind::Equal, &source[source.len() - suffix..]);
    out
}
pub fn positions(source: &str, target: &str) -> Option<Vec<usize>> {
    let a: Vec<_> = source.encode_utf16().collect();
    let b: Vec<_> = target.encode_utf16().collect();
    let mut positions = vec![0; a.len() + 1];
    let (mut from, mut to) = (0, 0);
    for edit in diff(&a, &b) {
        if edit.kind != Kind::Equal && edit.text.contains(&10) {
            return None;
        }
        if edit.kind == Kind::Insert {
            to += edit.text.len();
            continue;
        }
        for _ in edit.text {
            positions[from] = to;
            from += 1;
            if edit.kind == Kind::Equal {
                to += 1;
            }
        }
    }
    if from != a.len() || to != b.len() {
        return None;
    }
    positions[0] = 0;
    positions[a.len()] = b.len();
    Some(positions)
}
