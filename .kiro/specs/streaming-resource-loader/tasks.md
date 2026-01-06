# Implementation Plan: Streaming Resource Loader

## Overview

本实现计划将流式资源加载系统分解为可执行的编码任务。实现顺序遵循依赖关系：先配置，再核心组件，最后集成。

系统包含两部分：
1. **客户端**：修改MLibrary支持流式加载，添加ResourceHelper处理下载
2. **服务端**：创建MicroEndServer ASP.NET Core Web API，提供资源下载接口

## Tasks

- [x] 1. 添加Settings配置项
  - 在Client/Settings.cs中添加MicroClientEnabled、MicroClientIP、MicroClientPort、MicroClientPath配置项
  - 在Load方法中读取这些配置
  - 在Save方法中保存这些配置
  - _Requirements: 7.1, 7.2, 7.3, 7.5_

- [ ]* 1.1 编写Settings配置持久化属性测试
  - **Property 9: Settings Persistence Round-Trip**
  - **Validates: Requirements 7.5**

- [x] 2. 创建ResourceHelper核心类
  - [x] 2.1 创建Client/MirGraphics/ResourceHelper.cs文件
    - 添加HttpClient静态实例
    - 添加ServerActive属性
    - 添加Semaphore信号量（限制10并发）
    - 添加ExceptionCount和RetryTime字段
    - 添加PendingWrites字典和PendingImageWrite类
    - _Requirements: 1.1, 3.6, 5.1_

  - [x] 2.2 实现CheckServerOnline方法
    - 发送测试请求检查服务器状态
    - 更新ServerActive属性
    - _Requirements: 1.1, 1.2_

  - [x] 2.3 实现GetHeaderAsync方法
    - 构建API URL: /api/libheader/path/name
    - 下载头文件数据，解析TotalLength和HeaderBytes
    - 创建预分配大小的本地文件，写入头部数据
    - _Requirements: 2.1, 2.2, 2.4, 2.5_

  - [x] 2.4 实现GetImageAsync方法
    - 构建API URL: /api/libimage/path/name/index
    - 使用信号量控制并发，下载图片数据
    - 将数据添加到PendingWrites队列
    - 处理异常并增加ExceptionCount
    - _Requirements: 3.1, 3.2, 3.6, 8.1, 8.2_

  - [x] 2.5 实现ProcessPendingWrites方法
    - 遍历PendingWrites字典，批量写入数据到对应文件位置
    - 使用文件锁防止并发冲突，清空已处理的写入队列
    - _Requirements: 6.1, 6.2, 6.3, 6.4_

- [ ]* 2.6 编写并发限制属性测试
  - **Property 3: Concurrency Limit Enforcement**
  - **Validates: Requirements 3.6, 5.1**

- [ ]* 2.7 编写异常计数阈值属性测试
  - **Property 8: Exception Counter Threshold**
  - **Validates: Requirements 8.2, 8.3**

- [ ] 3. Checkpoint - 确保ResourceHelper编译通过
  - 确保所有代码编译通过，询问用户是否有问题

- [x] 4. 修改MImage类支持流式加载
  - [x] 4.1 在Client/MirGraphics/MLibrary.cs的MImage类中添加下载状态字段
    - 添加_downloadStatus字段（0=未下载, 1=下载中, 2=完成）
    - 添加_nextRetryTime字段
    - _Requirements: 3.1, 3.5_

  - [x] 4.2 实现GetPlaceholderTexture静态方法
    - 创建1x1透明纹理作为占位符，缓存避免重复创建
    - _Requirements: 4.1, 4.2_

  - [x] 4.3 实现CreateTextureFromData方法
    - 从下载的字节数组创建纹理，更新TextureValid状态
    - _Requirements: 3.4_

- [ ]* 4.4 编写纹理状态一致性属性测试
  - **Property 4: Texture State Consistency**
  - **Validates: Requirements 4.1, 4.3**

- [x] 5. 修改MLibrary类支持流式加载
  - [x] 5.1 在MLibrary类中添加流式加载状态字段
    - 添加_headerStatus、_nextRetryTime、_loadLock字段
    - _Requirements: 2.1, 2.3_

  - [x] 5.2 修改Initialize方法支持流式加载
    - 检查MicroClientEnabled配置
    - 如果文件不存在且启用流式加载，触发头文件下载
    - 保持原有本地加载逻辑
    - _Requirements: 2.1, 7.4, 9.2_

  - [x] 5.3 修改CheckImage方法支持按需下载
    - 检查图片数据是否为空，如果为空触发异步下载
    - 下载期间返回占位纹理
    - _Requirements: 3.1, 4.1, 9.1_

  - [x] 5.4 实现IsImageDataEmpty辅助方法
    - 读取图片数据区域，检查是否全为零字节
    - _Requirements: 3.1_

- [ ]* 5.5 编写空数据触发下载属性测试
  - **Property 7: Empty Data Triggers Download**
  - **Validates: Requirements 3.1**

- [ ]* 5.6 编写禁用模式行为属性测试
  - **Property 5: Disabled Mode Behavior**
  - **Validates: Requirements 7.4, 9.2**

- [ ] 6. Checkpoint - 确保MLibrary修改编译通过
  - 确保所有代码编译通过，询问用户是否有问题

- [x] 7. 集成到游戏主循环
  - [x] 7.1 在CMain.cs的UpdateEnviroment方法中调用ProcessPendingWrites
    - _Requirements: 6.3_

  - [x] 7.2 添加服务器状态检查和重试逻辑
    - 在适当位置检查服务器状态，实现15秒重试间隔
    - 当ServerActive为false时显示警告消息
    - _Requirements: 1.2, 1.3, 1.4_

- [ ]* 7.3 编写重试时间一致性属性测试
  - **Property 1: Retry Timing Consistency**
  - **Validates: Requirements 1.2, 2.3, 3.5**

- [ ] 8. Final Checkpoint - 确保所有测试通过
  - 运行所有单元测试和属性测试
  - 确保所有测试通过，询问用户是否有问题

- [x] 9. 创建资源服务器项目
  - [x] 9.1 创建MicroEndServer ASP.NET Core Web API项目
    - 创建MicroEndServer/MicroEndServer.csproj
    - 创建MicroEndServer/Program.cs，配置Kestrel和CORS
    - 创建MicroEndServer/appsettings.json，包含Port和ResourcePath配置
    - _Requirements: 10.3, 11.1, 11.2, 11.4_

  - [x] 9.2 实现LibraryParser服务
    - 创建MicroEndServer/Services/LibraryParser.cs
    - 实现ParseHeader方法：读取版本号、图片数量、索引数组
    - 实现GetImageData方法：根据索引计算偏移量，读取图片数据
    - 实现GetImageOffset辅助方法：计算图片在文件中的位置和长度
    - _Requirements: 12.1, 12.2, 12.3, 12.4_

  - [x] 9.3 实现LibraryController API控制器
    - 创建MicroEndServer/Controllers/LibraryController.cs
    - 实现GET /api/ping端点：返回"pong"
    - 实现GET /api/libheader/{path}/{name}端点：返回库文件头JSON
    - 实现GET /api/libimage/{path}/{name}/{index}端点：返回图片二进制数据
    - 添加错误处理：404文件不存在，500内部错误
    - _Requirements: 10.1, 10.2, 10.3, 10.5, 10.6, 10.7, 10.8_

- [ ]* 9.4 编写服务器头响应完整性属性测试
  - **Property 12: Server Header Response Integrity**
  - **Validates: Requirements 10.1, 10.5**

- [ ]* 9.5 编写服务器图片数据正确性属性测试
  - **Property 13: Server Image Data Correctness**
  - **Validates: Requirements 10.2, 10.6, 12.2, 12.3**

- [ ]* 9.6 编写服务器错误响应一致性属性测试
  - **Property 14: Server Error Response Consistency**
  - **Validates: Requirements 10.7, 10.8**

- [x] 10. Checkpoint - 确保资源服务器编译通过
  - 确保所有代码编译通过
  - 手动测试ping端点
  - 询问用户是否有问题

- [x] 11. 集成测试
  - [x] 11.1 添加服务器项目到解决方案
    - 更新Legend of Mir.sln，添加MicroEndServer项目引用
    - _Requirements: 10.1_

  - [x] 11.2 添加请求日志中间件
    - 在Program.cs中添加请求日志记录
    - _Requirements: 11.3_

- [ ] 12. Final Checkpoint - 端到端测试
  - 启动资源服务器
  - 配置客户端连接到本地服务器
  - 测试完整的流式加载流程
  - 询问用户是否有问题

- [x] 13. 修复服务端路径解析问题
  - [x] 13.1 修复LibraryParser.GetFilePath方法
    - 添加调试日志输出实际解析的路径
    - 修复路径拼接逻辑，确保正确映射客户端路径到服务端文件系统
    - 支持多种路径格式（Data/xxx.Lib, Data_Map_xxx/xxx.Lib等）
    - _Requirements: 12.5_

  - [x] 13.2 添加服务端路径调试端点
    - 添加GET /api/debug/path/{path}/{name}端点，返回解析后的实际路径
    - 用于调试路径映射问题
    - _Requirements: 11.3_

- [x] 14. 实现场景感知的按需加载
  - [x] 14.1 重构Libraries静态类
    - 将库文件分组：登录场景库、游戏场景库、地图库
    - 移除静态构造函数中的全量初始化
    - 添加InitializeForLogin()方法，只初始化登录所需库
    - 添加InitializeForGame()方法，初始化游戏场景所需库
    - _Requirements: 13.1, 13.2, 13.5_

  - [x] 14.2 实现MapLibs的延迟加载
    - 修改MapLibs数组为延迟初始化
    - 只在地图加载时初始化对应的MapLib
    - _Requirements: 13.3, 13.4_

  - [x] 14.3 修改游戏启动流程
    - 在登录场景只调用InitializeForLogin()
    - 在进入游戏场景时调用InitializeForGame()
    - _Requirements: 13.1, 13.2_

- [x] 15. 增强错误处理
  - [x] 15.1 修复MLibrary.Initialize中的异常处理
    - 捕获EndOfStreamException，不创建损坏的MImage对象
    - 验证文件完整性后再读取
    - _Requirements: 14.1, 14.4_

  - [x] 15.2 修复CheckImage方法的错误处理
    - 当文件不完整时返回占位纹理而不是崩溃
    - 添加try-catch保护所有文件读取操作
    - _Requirements: 14.2_

  - [x] 15.3 修复GetHeaderAsync的文件创建逻辑
    - 只在下载成功且数据有效时才创建本地文件
    - 下载失败时不创建空文件
    - _Requirements: 14.3_

- [x] 16. Final Checkpoint - 验证修复
  - 验证服务端路径解析正确
  - 验证登录界面只加载必要资源
  - 验证下载失败时不会崩溃
  - 询问用户是否有问题

- [ ] 17. 修复UI对话框居中定位问题
  - [x] 17.1 修复登录场景对话框
    - 修复LoginDialog：在构造函数中显式设置Size为(328, 220)后再计算Location
    - 修复InputKeyDialog：获取Size，如果为(0,0)则使用默认大小(204, 268)
    - 修复NewAccountDialog：获取Size，如果为(0,0)则使用默认大小(568, 467)
    - 修复ChangePasswordDialog：获取Size，如果为(0,0)则使用默认大小(370, 280)
    - _Requirements: 15.1, 15.2, 15.3, 15.5_

  - [x] 17.2 修复角色选择场景对话框
    - 修复NewCharacterDialog：获取Size，如果为(0,0)则使用默认大小(568, 467)
    - _Requirements: 15.1, 15.2, 15.3, 15.5_

  - [x] 17.3 修复游戏场景对话框（部分完成）
    - 修复NoticeDialog：获取Size，如果为(0,0)则使用默认大小(320, 470)
    - 修复RankingDialog：获取Size，如果为(0,0)则使用默认大小
    - 修复OptionDialog：获取Size，如果为(0,0)则使用默认大小
    - _Requirements: 15.1, 15.2, 15.3, 15.4, 15.5_

  - [x] 17.4 修复其他居中定位对话框
    - 修复ChatOptionDialog：获取Size，如果为(0,0)则使用默认大小
    - 修复ItemRentalDialog：已设置Size，无需修复
    - 修复ChatNoticeDialog：获取Size，如果为(0,0)则使用默认大小
    - 修复MirInputBox：获取Size，如果为(0,0)则使用默认大小
    - 修复MirMessageBox：获取Size，如果为(0,0)则使用默认大小
    - 修复MirAmountBox：获取Size，如果为(0,0)则使用默认大小
    - _Requirements: 15.1, 15.2, 15.3, 15.4, 15.5_

- [ ]* 18. 可选优化：实现基础控件居中定位增强
  - [ ]* 18.1 在MirImageControl中添加CenterOnScreen属性
    - 添加_centerOnScreen私有字段和CenterOnScreen公共属性
    - 默认值为false以保持向后兼容
    - _Requirements: 16.1, 16.3_

  - [ ]* 18.2 实现自动居中逻辑
    - 在DrawControl或OnSizeChanged中检测Size从(0,0)变为有效值
    - 当CenterOnScreen为true时自动重新计算Location
    - _Requirements: 16.2_

  - [ ]* 18.3 重构现有对话框使用CenterOnScreen
    - 将已修复的对话框改为使用CenterOnScreen属性
    - 移除手动设置Size和默认大小的代码
    - _Requirements: 16.4_

- [ ] 19. Final Checkpoint - 验证UI定位修复
  - 启动微端模式，验证登录框居中显示
  - 验证注册框、修改密码框居中显示
  - 验证创建角色对话框居中显示
  - 验证非微端模式下对话框仍然正常工作
  - 询问用户是否有问题

- [x] 20. 实现非阻塞场景切换
  - [x] 20.1 修改SelectScene.StartGame方法
    - 移除Libraries.Loaded检查和等待逻辑
    - 直接发送StartGame请求到服务器
    - _Requirements: 17.1, 17.4_

  - [x] 20.2 修改StartGame服务器响应处理
    - 在收到成功响应后异步调用Libraries.InitializeForGame()
    - 立即创建GameScene并切换，不等待初始化完成
    - _Requirements: 17.1, 17.2, 17.3_

- [x] 21. 实现系统性UI组件定位修复
  - [x] 21.1 在MirImageControl中添加DefaultSize属性
    - 添加_defaultSize私有字段
    - 添加DefaultSize公共属性
    - _Requirements: 18.1_

  - [x] 21.2 修改MirImageControl.Size属性getter
    - 当AutoSize为true且实际大小为(0,0)时，返回DefaultSize
    - 当base.Size为(0,0)时，返回DefaultSize
    - _Requirements: 18.2, 18.4_

  - [x] 21.3 为所有居中定位对话框设置DefaultSize
    - LoginDialog: DefaultSize = (328, 220)
    - InputKeyDialog: DefaultSize = (204, 268)
    - NewAccountDialog: DefaultSize = (568, 467)
    - ChangePasswordDialog: DefaultSize = (370, 280)
    - NewCharacterDialog: DefaultSize = (568, 467)
    - NoticeDialog: DefaultSize = (320, 470)
    - RankingDialog: DefaultSize = (288, 324)
    - OptionDialog: DefaultSize = (352, 412)
    - ChatOptionDialog: DefaultSize = (270, 180)
    - ChatNoticeDialog: DefaultSize = (260, 100)
    - MirInputBox: DefaultSize = (360, 110)
    - MirMessageBox: DefaultSize = (360, 150)
    - MirAmountBox: DefaultSize = (238, 175)
    - _Requirements: 18.3, 18.5_

- [x] 22. 修复地图黑块问题
  - [x] 22.1 修改MLibrary.Draw方法返回值
    - 将Draw方法返回类型改为bool
    - 当资源未加载或纹理无效时返回false
    - 绘制成功时返回true
    - _Requirements: 19.1_

  - [x] 22.2 添加地图重绘失效机制
    - 在MapControl中添加静态InvalidateFloor方法
    - 在MLibrary图片下载完成后调用InvalidateFloor
    - _Requirements: 19.2_

  - [x] 22.3 优化地图资源下载优先级
    - 在DrawFloor中记录需要但未加载的瓦片索引
    - 优先下载玩家可视范围内的地图资源
    - _Requirements: 19.3, 19.5_

- [ ] 23. Final Checkpoint - 验证所有修复
  - 启动微端模式，点击开始游戏后立即进入主场景
  - 验证所有UI组件位置正确
  - 验证地图无黑块，资源渐进加载
  - 验证非微端模式下游戏正常运行
  - 询问用户是否有问题

## Notes

- 任务标记 `*` 的为可选测试任务，可跳过以加快MVP开发
- 每个任务引用具体的需求编号以确保可追溯性
- Checkpoint任务用于增量验证，确保代码质量
- 属性测试验证通用正确性属性，需运行至少100次迭代
