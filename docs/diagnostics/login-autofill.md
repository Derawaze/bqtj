# 自动登录诊断

## 当前行为

创建与编辑共用表单，本地按账号Guid保存4399账号和密码；容器“账号密码”按钮只读查看，默认掩码。编辑后的凭据在下次启动生效。

游戏包装页就绪后调用平台UniLogin.showPopupUsernameLogin('','',true)打开账号密码方式，不向该函数传凭据，避免进入iframe URL。每份文档只打开一次，已登录或用户关闭后不反复弹出。

DocumentComplete、NavigateComplete2和最多60秒、500ms间隔的就绪检查遍历有限深度frames。通过来源与表单检查后填充username和j-password，锁定本轮尝试，再点击同一表单的可用j-login-submit-btn一次。保留平台onsubmit校验/加密，不直接form.submit；错误与验证码交由用户处理。

## 不可丢失的修复

- action实际为/ptlogin/login.do?v=1。只允许精确HTTPS登录文档中的固定根相对路径，无base标签；绝对地址也必须精确匹配。
- 网站把“请输入4399账号或手机号”写进username.value模拟占位符。只将该固定文字视为空；真实非空用户名/密码不覆盖。填充后去灰色样式，不触发会发送用户名请求的focus/blur。
- 凭据从SQLite经绑定的父子标准输入管道传递，限长UTF-16十六进制编码防止换行注入；编码不是加密。不走命令行、环境变量、剪贴板或URL。
- 原生UI线程持有副本，刷新重新绑定浏览器事件，退出清零缓冲区。字段类型、同一表单、可编辑状态和精确来源必须通过；失败保留手动登录。

## 已有证据与复验

用户已验收自动填充；.6/.7实机仅点击启动游戏后自动进入游戏加载及启动菜单。精简正式包的验证范围见 [交付记录](../release-v0.1.0.md)。

LoginAutofillProbe覆盖来源拒绝、占位符、手动内容保护、延迟表单以及实际ATL浏览器的单次点击、禁用按钮和其他表单按钮不提交。CredentialPipeProbe覆盖换行、引号、冒号的虚构凭据传输。LiveLoginAutofillProbe曾复现旧填充失败，修复后保持两个虚构字段五秒并回读通过；默认不提交网络登录。

在仓库根目录使用x86 GCC，先确保artifacts目录存在：

```powershell
gcc -DUNICODE -std=c11 -o artifacts/LoginAutofillProbe.exe native/Diagnostics/LoginAutofillProbe.c -lole32 -loleaut32 -luuid
./artifacts/LoginAutofillProbe.exe
gcc -DUNICODE -o artifacts/LiveLoginAutofillProbe.exe native/Diagnostics/LiveLoginAutofillProbe.c native/FlashHost/virtual_clock.c -lole32 -loleaut32 -lshell32 -lwininet -luuid -lgdi32
./artifacts/LiveLoginAutofillProbe.exe
```

探针不输出凭据、Cookie、页面全文或URL查询串。离线DOM成功不等于平台认证成功；只有根文档、无iframe时也不能判定填充成功。

