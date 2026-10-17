use crate::diff::positions;
use indexmap::IndexMap;
use serde_json::Value;
pub const FORMAT: &str = "Chromium Web Custom MIME Data Format";
const SLICE: &str = "application/x-vnd.google-docs-document-slice-clip+wrapped";
const CLIP_ID: &str = "application/x-vnd.google-docs-internal-clip-id";
pub fn read_map(bytes: &[u8]) -> Result<IndexMap<String, String>, String> {
    if bytes.len() < 8 || bytes.len() > 1024 * 1024 {
        return Err("invalid_native_data".into());
    }
    let mut cursor = 0;
    fn integer(bytes: &[u8], cursor: &mut usize) -> Result<usize, String> {
        let b = bytes
            .get(*cursor..*cursor + 4)
            .ok_or("invalid_native_data")?;
        *cursor += 4;
        Ok(u32::from_le_bytes(b.try_into().unwrap()) as usize)
    }
    if integer(bytes, &mut cursor)? != bytes.len() - 4 {
        return Err("invalid_native_data".into());
    }
    let count = integer(bytes, &mut cursor)?;
    if count > 64 {
        return Err("invalid_native_data".into());
    }
    let mut map = IndexMap::new();
    for _ in 0..count {
        let mut strings = Vec::new();
        for _ in 0..2 {
            let chars = integer(bytes, &mut cursor)?;
            let len = chars.checked_mul(2).ok_or("invalid_native_data")?;
            let end = cursor.checked_add(len).ok_or("invalid_native_data")?;
            let raw = bytes.get(cursor..end).ok_or("invalid_native_data")?;
            let utf16: Vec<u16> = raw
                .as_chunks::<2>()
                .0
                .iter()
                .map(|b| u16::from_le_bytes([b[0], b[1]]))
                .collect();
            strings.push(String::from_utf16(&utf16).map_err(|_| "invalid_native_data")?);
            cursor = (end + 3) & !3;
            if cursor > bytes.len() {
                return Err("invalid_native_data".into());
            }
        }
        if map.insert(strings.remove(0), strings.remove(0)).is_some() {
            return Err("invalid_native_data".into());
        }
    }
    if cursor != bytes.len() {
        return Err("invalid_native_data".into());
    }
    Ok(map)
}
pub fn write_map(map: &IndexMap<String, String>) -> Result<Vec<u8>, String> {
    let mut bytes = vec![0u8; 4];
    bytes.extend_from_slice(&(map.len() as u32).to_le_bytes());
    for (k, v) in map {
        for text in [k, v] {
            let units: Vec<u16> = text.encode_utf16().collect();
            bytes.extend_from_slice(&(units.len() as u32).to_le_bytes());
            for u in units {
                bytes.extend_from_slice(&u.to_le_bytes());
            }
            while !bytes.len().is_multiple_of(4) {
                bytes.push(0);
            }
        }
    }
    if bytes.len() > 1024 * 1024 {
        return Err("invalid_native_data".into());
    }
    let n = (bytes.len() - 4) as u32;
    bytes[..4].copy_from_slice(&n.to_le_bytes());
    Ok(bytes)
}
pub fn rewrite(bytes: &[u8], copied: &str, replacement: &str) -> Result<Vec<u8>, String> {
    let mut map = read_map(bytes)?;
    let wrapped = map.get(SLICE).ok_or("unsupported_mime")?;
    let mut wrapper: Value = serde_json::from_str(wrapped).map_err(|_| "invalid_native_data")?;
    let mut slice: Value =
        serde_json::from_str(wrapper["data"].as_str().ok_or("invalid_native_data")?)
            .map_err(|_| "invalid_native_data")?;
    let resolved = slice
        .get_mut("resolved")
        .and_then(Value::as_object_mut)
        .ok_or("invalid_native_data")?;
    let source = resolved
        .get("dsl_spacers")
        .and_then(Value::as_str)
        .ok_or("invalid_native_data")?
        .to_owned();
    let copied = copied.replace("\r\n", "\n");
    if source != copied && format!("{source}\n") != copied {
        return Err("source_mismatch".into());
    }
    let trim = source.ends_with('\n')
        && source.matches('\n').count() == replacement.matches('\n').count() + 1;
    let alignment = if trim {
        &source[..source.len() - 1]
    } else {
        &source
    };
    if alignment.matches('\n').count() != replacement.matches('\n').count() {
        return Err("paragraph_mismatch".into());
    }
    for key in [
        "dsl_entitypositionmap",
        "dsl_relateddocslices",
        "dsl_nestedmodelmap",
        "dsl_drawingrevisionaccesstokenmap",
    ] {
        if !resolved
            .get(key)
            .and_then(Value::as_object)
            .is_some_and(|o| o.is_empty())
        {
            return Err("unsupported_embedded_content".into());
        }
    }
    for key in ["dsl_suggestedinsertions", "dsl_suggesteddeletions"] {
        if !resolved
            .get(key)
            .and_then(|v| v.get("sgsl_sugg"))
            .and_then(Value::as_array)
            .is_some_and(|o| o.is_empty())
        {
            return Err("unsupported_suggestions".into());
        }
    }
    let positions = positions(alignment, replacement).ok_or("paragraph_edit")?;
    let old_len = source.encode_utf16().count();
    for key in ["dsl_styleslices", "dsl_metastyleslices"] {
        let styles = resolved
            .get_mut(key)
            .and_then(Value::as_array_mut)
            .ok_or("unsupported_styles")?;
        for style in styles {
            let old = style["stsl_styles"]
                .as_array()
                .ok_or("invalid_native_data")?
                .clone();
            let mut mapped = Vec::new();
            for (i, value) in old.into_iter().enumerate() {
                if value.is_null() {
                    continue;
                }
                if i > old_len {
                    return Err("invalid_style_offset".into());
                }
                if trim
                    && i == old_len - 1
                    && matches!(style["stsl_type"].as_str(), Some("paragraph" | "list"))
                {
                    style["stsl_trailing"] = value;
                    style["stsl_trailingType"] = style["stsl_type"].clone();
                    continue;
                }
                let position = positions[i.min(positions.len() - 1)];
                if mapped.len() <= position {
                    mapped.resize(position + 1, Value::Null);
                }
                mapped[position] = value;
            }
            style["stsl_styles"] = Value::Array(mapped);
        }
    }
    resolved.insert("dsl_spacers".into(), Value::String(replacement.into()));
    if !slice["autotext_content"]
        .as_object()
        .is_some_and(|o| o.is_empty())
    {
        return Err("unsupported_autotext".into());
    }
    wrapper["data"] = Value::String(dotnet_json(&slice));
    map.insert(SLICE.into(), dotnet_json(&wrapper));
    map.shift_remove(CLIP_ID);
    write_map(&map)
}
// Match System.Text.Json escaping in Chromium's nested JSON strings to retain byte parity.
fn dotnet_json(value: &Value) -> String {
    fn string(text: &str) -> String {
        let mut out = String::from("\"");
        for c in text.chars() {
            match c {
                '\n' => out.push_str("\\n"),
                '\r' => out.push_str("\\r"),
                '\t' => out.push_str("\\t"),
                '\u{8}' => out.push_str("\\b"),
                '\u{c}' => out.push_str("\\f"),
                '\\' => out.push_str("\\\\"),
                c if !c.is_ascii()
                    || c.is_control()
                    || matches!(c, '"' | '\'' | '&' | '+' | '<' | '>' | '`') =>
                {
                    let mut b = [0; 2];
                    for u in c.encode_utf16(&mut b) {
                        out.push_str(&format!("\\u{u:04X}"));
                    }
                }
                c => out.push(c),
            }
        }
        out.push('"');
        out
    }
    match value {
        Value::String(s) => string(s),
        Value::Array(a) => format!(
            "[{}]",
            a.iter().map(dotnet_json).collect::<Vec<_>>().join(",")
        ),
        Value::Object(o) => format!(
            "{{{}}}",
            o.iter()
                .map(|(k, v)| format!("{}:{}", string(k), dotnet_json(v)))
                .collect::<Vec<_>>()
                .join(",")
        ),
        other => other.to_string(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn pickle_roundtrip_and_bounds() {
        let map = IndexMap::from([("emoji 🦀".into(), "payload".into())]);
        assert_eq!(read_map(&write_map(&map).unwrap()).unwrap(), map);
        assert!(read_map(&[0; 8]).is_err());
        for n in 0..40 {
            assert!(read_map(&vec![255; n]).is_err());
        }
    }
}
