# 第三方组件声明

本项目包含以下第三方资源，其版权归原作者所有。

## DM Sans 字体

`index.html` 内联了 DM Sans 可变字体（字重 400–700）的拉丁子集（woff2，base64 编码），
仅用于界面与落地页的英文与数字显示，中文回退系统字体。

- 字体名称：DM Sans
- 版权：Copyright 2014 The DM Sans Project Authors
- 许可证：SIL Open Font License, Version 1.1
- 来源：https://github.com/googlefonts/dm-fonts
- 许可证全文：https://openfontlicense.org/open-font-license-official-text/

## Inno Setup 简体中文语言文件

`installer/ChineseSimplified.isl` 来自 Inno Setup 官方语言包。

- 版权：Copyright (C) 1997-2024 Jordan Russell / Martijn Laan 及语言文件贡献者
- 许可证：Inno Setup License（允许随安装包自由分发）
- 来源：https://github.com/jrsoftware/issrc

## WebView2 运行时引导器

`installer/redist/MicrosoftEdgeWebview2Setup.exe` 由 Microsoft 提供，
**不入库**，由 `build-installer.ps1` 在构建时从官方地址下载。

- 版权：Copyright (C) Microsoft Corporation
- 下载地址：https://go.microsoft.com/fwlink/p/?LinkId=2124703

## 其他

- .NET 8 运行时：MIT License，Copyright (C) .NET Foundation
- Microsoft.Web.WebView2：Microsoft 软件许可条款
- 日出日落算法：NOAA 通用近似公式，属公共领域算法
