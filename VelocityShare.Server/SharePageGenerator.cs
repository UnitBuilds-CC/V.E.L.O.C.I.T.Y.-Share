using System;
using System.Text;

namespace VelocityShare.Server;

/// <summary>
/// Generates HTML pages for share link downloads.
/// </summary>
public static class SharePageGenerator
{
    private const string GoogleFont = "https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&display=swap";

    private static string PageShell(string title, string styles, string body)
    {
        return "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"UTF-8\">" +
               "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
               "<title>" + title + "</title>" +
               "<link href=\"" + GoogleFont + "\" rel=\"stylesheet\">" +
               "<style>" + styles + "</style></head><body>" + body + "</body></html>";
    }

    private const string BaseCss = "*{margin:0;padding:0;box-sizing:border-box}" +
        "body{font-family:'Inter',sans-serif;background:#0a0c12;color:#f1f5f9;min-height:100vh;display:flex;align-items:center;justify-content:center}" +
        ".card{background:rgba(14,18,28,0.85);border:1px solid rgba(255,255,255,0.06);border-radius:16px;padding:48px;text-align:center;max-width:420px;width:100%}";

    public static string GenerateExpiredPage()
    {
        var css = BaseCss +
            ".icon{width:64px;height:64px;margin:0 auto 24px;border-radius:50%;background:rgba(239,68,68,0.1);display:flex;align-items:center;justify-content:center;color:#ef4444;font-size:28px}" +
            "h1{font-size:20px;font-weight:600;margin-bottom:8px}p{color:#94a3b8;font-size:14px;line-height:1.5}";
        var body = "<div class=\"card\"><div class=\"icon\">&#10006;</div>" +
                   "<h1>Link Expired</h1>" +
                   "<p>This share link has expired or has reached its download limit. Please ask the sender to create a new link.</p></div>";
        return PageShell("Link Expired - V.E.L.O.C.I.T.Y. Share", css, body);
    }

    public static string GeneratePasswordPage(string shareId)
    {
        var css = BaseCss + "max-width:400px}" +
            ".icon{width:64px;height:64px;margin:0 auto 24px;border-radius:50%;background:rgba(245,158,11,0.1);display:flex;align-items:center;justify-content:center;color:#f59e0b;font-size:28px}" +
            "h1{font-size:20px;font-weight:600;margin-bottom:8px}p{color:#94a3b8;font-size:14px;margin-bottom:24px}" +
            "input{width:100%;padding:12px 16px;border-radius:8px;border:1px solid rgba(255,255,255,0.1);background:rgba(255,255,255,0.04);color:#f1f5f9;font-size:14px;margin-bottom:16px;outline:none}" +
            "input:focus{border-color:#00ff66}" +
            "button{width:100%;padding:12px;border-radius:8px;border:none;background:#00ff66;color:#0a0c12;font-weight:600;font-size:14px;cursor:pointer}button:hover{background:#00e55c}" +
            ".error{color:#ef4444;font-size:13px;margin-top:-8px;margin-bottom:16px;display:none}";
        var body = "<div class=\"card\"><div class=\"icon\">&#128274;</div>" +
                   "<h1>Password Protected</h1>" +
                   "<p>This file is password protected. Enter the password to download.</p>" +
                   "<form id=\"pwform\"><input type=\"password\" id=\"pw\" placeholder=\"Enter password\" autofocus>" +
                   "<p class=\"error\" id=\"err\">Wrong password. Try again.</p>" +
                   "<button type=\"submit\">Unlock &amp; Download</button></form></div>" +
                   "<script>" +
                   "document.getElementById('pwform').onsubmit=async function(e){" +
                   "e.preventDefault();" +
                   "var pw=document.getElementById('pw').value;" +
                   "var r=await fetch('/s/" + shareId + "/verify',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({password:pw})});" +
                   "if(r.ok){var d=await r.json();window.location='/s/" + shareId + "/download?token='+encodeURIComponent(d.downloadToken)}" +
                   "else{document.getElementById('err').style.display='block'}" +
                   "}" +
                   "</script>";
        return PageShell("Password Required - V.E.L.O.C.I.T.Y. Share", css, body);
    }

    public static string GenerateDownloadPage(string shareId, string fileName, string sizeStr, string expiresAt, int downloadsRemaining)
    {
        var encodedName = System.Net.WebUtility.HtmlEncode(fileName);
        var css = BaseCss +
            ".icon{width:72px;height:72px;margin:0 auto 24px;border-radius:50%;background:rgba(0,255,102,0.08);display:flex;align-items:center;justify-content:center;color:#00ff66;font-size:32px}" +
            "h1{font-size:18px;font-weight:600;margin-bottom:8px;word-break:break-all}" +
            ".meta{color:#94a3b8;font-size:13px;margin-bottom:32px}.meta span{display:block;margin:4px 0}" +
            "a.download{display:inline-flex;align-items:center;gap:8px;padding:14px 32px;border-radius:10px;background:#00ff66;color:#0a0c12;font-weight:600;font-size:15px;text-decoration:none;transition:all 0.2s}" +
            "a.download:hover{background:#00e55c;transform:translateY(-1px)}" +
            ".footer{margin-top:32px;color:#64748b;font-size:12px}";
        var body = "<div class=\"card\"><div class=\"icon\">&#8681;</div>" +
                   "<h1>" + encodedName + "</h1>" +
                   "<div class=\"meta\"><span>Size: " + sizeStr + "</span>" +
                   "<span>Expires: " + expiresAt + "</span>" +
                   "<span>Downloads remaining: " + downloadsRemaining + "</span></div>" +
                   "<a class=\"download\" href=\"/s/" + shareId + "/download\">&#8681; Download File</a>" +
                   "<div class=\"footer\">Sent via V.E.L.O.C.I.T.Y. Share</div></div>";
        return PageShell("Download " + encodedName + " - V.E.L.O.C.I.T.Y. Share", css, body);
    }

    public static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
