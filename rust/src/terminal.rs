use regex::Regex;
use std::sync::LazyLock;
macro_rules! re {
    ($p:literal) => {{
        static R: LazyLock<Regex> = LazyLock::new(|| Regex::new($p).unwrap());
        &*R
    }};
}
pub fn normalize(text: &str, process: &str) -> String {
    if ![
        "WindowsTerminal",
        "Code",
        "powershell",
        "pwsh",
        "cmd",
        "bash",
    ]
    .iter()
    .any(|p| p.eq_ignore_ascii_case(process))
    {
        return text.into();
    }
    let text = re!(r"\r\n[ \t]*▎[ \t]*\r\n")
        .replace_all(text, "\r\n\r\n")
        .replace('▎', "");
    let paths = re!(r#"(?:[A-Za-z]:\\|\\\\[^\\\s]+\\)(?:[^\r\n"'<>|:*?]|\r\n[ \t])+"#)
        .replace_all(&text, |cap: &regex::Captures| {
            re!(r" *\r\n[ \t]+").replace_all(&cap[0], "").into_owned()
        });
    let text = re!(r"\r\n\r\n[ \t]*").replace_all(&paths, "\n\n");
    // Lookahead is unnecessary when the marker is captured and re-emitted unchanged.
    let text = re!(r"\r\n[ \t]+([-*•][ \t]|[0-9]+\.[ \t])").replace_all(&text, "\n$1");
    re!(r" *\r\n[ \t]+").replace_all(&text, " ").into_owned()
}
