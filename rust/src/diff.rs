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
    try_diff(source, target).unwrap_or_else(|| {
        let mut out = Vec::new();
        let prefix = source
            .iter()
            .zip(target)
            .take_while(|(a, b)| a == b)
            .count();
        let suffix = source[prefix..]
            .iter()
            .rev()
            .zip(target[prefix..].iter().rev())
            .take_while(|(a, b)| a == b)
            .count();
        push(&mut out, Kind::Equal, &source[..prefix]);
        push(
            &mut out,
            Kind::Delete,
            &source[prefix..source.len() - suffix],
        );
        push(
            &mut out,
            Kind::Insert,
            &target[prefix..target.len() - suffix],
        );
        push(&mut out, Kind::Equal, &source[source.len() - suffix..]);
        out
    })
}

// Rich-text consumers must distinguish a bounded refusal from a precise edit map.
pub fn try_diff(source: &[u16], target: &[u16]) -> Option<Vec<Edit>> {
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
        for edit in line_diff(a, b).or_else(|| sparse_diff(a, b))? {
            push(&mut out, edit.kind, &edit.text);
        }
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
    Some(out)
}

fn line_diff(a: &[u16], b: &[u16]) -> Option<Vec<Edit>> {
    let left: Vec<_> = a.split_inclusive(|c| *c == 10).collect();
    let right: Vec<_> = b.split_inclusive(|c| *c == 10).collect();
    if left.len() < 2
        || left.len() != right.len()
        || left
            .iter()
            .zip(&right)
            .any(|(a, b)| (a.last() == Some(&10)) != (b.last() == Some(&10)))
        || left
            .iter()
            .zip(&right)
            .map(|(a, b)| a.len().saturating_mul(b.len()).min(2_000_000))
            .try_fold(0usize, |sum, work| sum.checked_add(work))
            .is_none_or(|work| work > 2_000_000)
    {
        return None;
    }
    let mut out = Vec::new();
    for (a, b) in left.into_iter().zip(right) {
        for edit in try_diff(a, b)? {
            push(&mut out, edit.kind, &edit.text);
        }
    }
    Some(out)
}

fn sparse_diff(a: &[u16], b: &[u16]) -> Option<Vec<Edit>> {
    const MAX_EDITS: usize = 256;
    const MAX_WORK: usize = 2_000_000;
    let limit = MAX_EDITS.min(a.len() + b.len());
    let offset = limit as isize + 1;
    let mut frontier = vec![0isize; 2 * limit + 3];
    let mut trace = Vec::new();
    let mut work = 0;
    for depth in 0..=limit {
        for diagonal in (-(depth as isize)..=depth as isize).step_by(2) {
            work += 1;
            let slot = (offset + diagonal) as usize;
            let mut x = if diagonal == -(depth as isize)
                || (diagonal != depth as isize && frontier[slot - 1] < frontier[slot + 1])
            {
                frontier[slot + 1]
            } else {
                frontier[slot - 1] + 1
            };
            let mut y = x - diagonal;
            while x < a.len() as isize && y < b.len() as isize && a[x as usize] == b[y as usize] {
                x += 1;
                y += 1;
                work += 1;
                if work > MAX_WORK {
                    return None;
                }
            }
            if work > MAX_WORK {
                return None;
            }
            frontier[slot] = x;
            if x == a.len() as isize && y == b.len() as isize {
                let (mut x, mut y) = (a.len(), b.len());
                let mut reversed = Vec::new();
                for d in (1..=depth).rev() {
                    let previous: &Vec<isize> = &trace[d - 1];
                    let k = x as isize - y as isize;
                    let slot = (offset + k) as usize;
                    let previous_k = if k == -(d as isize)
                        || (k != d as isize && previous[slot - 1] < previous[slot + 1])
                    {
                        k + 1
                    } else {
                        k - 1
                    };
                    let previous_x = previous[(offset + previous_k) as usize] as usize;
                    let previous_y = (previous_x as isize - previous_k) as usize;
                    while x > previous_x && y > previous_y {
                        x -= 1;
                        y -= 1;
                        reversed.push((Kind::Equal, a[x]));
                    }
                    if x == previous_x {
                        y -= 1;
                        reversed.push((Kind::Insert, b[y]));
                    } else {
                        x -= 1;
                        reversed.push((Kind::Delete, a[x]));
                    }
                }
                while x > 0 && y > 0 {
                    x -= 1;
                    y -= 1;
                    reversed.push((Kind::Equal, a[x]));
                }
                let mut out = Vec::new();
                for (kind, unit) in reversed.into_iter().rev() {
                    push(&mut out, kind, &[unit]);
                }
                return Some(out);
            }
        }
        trace.push(frontier.clone());
    }
    None
}
pub fn positions(source: &str, target: &str) -> Option<Vec<usize>> {
    let a: Vec<_> = source.encode_utf16().collect();
    let b: Vec<_> = target.encode_utf16().collect();
    let mut positions = vec![0; a.len() + 1];
    let (mut from, mut to) = (0, 0);
    for edit in try_diff(&a, &b)? {
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

#[cfg(test)]
mod tests {
    use super::*;
    fn check(a: &[u16], b: &[u16], edits: &[Edit]) {
        let consumed: Vec<_> = edits
            .iter()
            .filter(|e| e.kind != Kind::Insert)
            .flat_map(|e| e.text.iter().copied())
            .collect();
        let produced: Vec<_> = edits
            .iter()
            .filter(|e| e.kind != Kind::Delete)
            .flat_map(|e| e.text.iter().copied())
            .collect();
        assert_eq!(consumed, a);
        assert_eq!(produced, b);
    }
    #[test]
    fn bounded_sparse_map_matches_small_optimal_edit_costs() {
        let mut words = vec![vec![]];
        for length in 1..=4 {
            for mask in 0..1usize << length {
                words.push(
                    (0..length)
                        .map(|bit| if mask & (1 << bit) == 0 { 97 } else { 98 })
                        .collect(),
                );
            }
        }
        for a in &words {
            for b in &words {
                let edits = sparse_diff(a, b).unwrap();
                check(a, b, &edits);
                let cost = |edits: &[Edit]| {
                    edits
                        .iter()
                        .filter(|e| e.kind != Kind::Equal)
                        .map(|e| e.text.len())
                        .sum::<usize>()
                };
                assert_eq!(cost(&edits), cost(&try_diff(a, b).unwrap()));
            }
        }
    }
    #[test]
    fn large_sparse_changes_retain_unchanged_boundaries() {
        let source = format!(
            "teh {}\nteh {} end",
            "alpha ".repeat(800),
            "beta ".repeat(800)
        );
        let target = source.replace("teh", "the");
        let (a, b): (Vec<_>, Vec<_>) = (
            source.encode_utf16().collect(),
            target.encode_utf16().collect(),
        );
        let edits = try_diff(&a, &b).unwrap();
        check(&a, &b, &edits);
        assert!(
            edits
                .iter()
                .filter(|e| e.kind != Kind::Equal)
                .all(|e| !e.text.contains(&10))
        );
        assert!(positions(&source, &target).is_some());
        let source = format!("teh {}teh end", "🦀 café ".repeat(500));
        let target = source.replace("teh", "the");
        let a: Vec<_> = source.encode_utf16().collect();
        let b: Vec<_> = target.encode_utf16().collect();
        let edits = try_diff(&a, &b).unwrap();
        check(&a, &b, &edits);
        let source = "teh sample words\n".repeat(200);
        let target = source.replace("teh", "the");
        assert!(positions(&source, &target).is_some());
    }
    #[test]
    fn precise_consumers_refuse_unbounded_wholesale_rewrites() {
        let a = vec![97; 5000];
        let b = vec![98; 5000];
        assert!(try_diff(&a, &b).is_none());
        check(&a, &b, &diff(&a, &b));
    }
}
