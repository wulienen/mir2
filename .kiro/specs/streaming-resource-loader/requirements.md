# Requirements Document

## Introduction

本功能旨在为PC游戏客户端实现"边玩边下载"的流式资源加载系统。参考安卓微端的实现方式，允许玩家在只下载少量核心资源后即可开始游戏，游戏过程中按需下载并渲染所需的图片资源。这将大幅减少玩家首次进入游戏的等待时间，提升用户体验。

## Glossary

- **Streaming_Resource_Loader**: 流式资源加载器，负责协调资源的按需下载和本地缓存
- **Resource_Server**: 资源服务器，提供游戏资源的HTTP API下载服务，基于ASP.NET Core实现
- **Library_File**: 库文件（.Lib），包含多个图片资源的打包文件
- **Library_Header**: 库文件头，包含库文件的索引信息和元数据（版本号、图片数量、索引数组）
- **MImage**: 单个图片资源，包含纹理数据和元信息
- **Placeholder_Texture**: 占位纹理，在资源下载完成前显示的临时图像
- **Download_Queue**: 下载队列，管理待下载资源的优先级和并发控制
- **Local_Cache**: 本地缓存，存储已下载资源的本地文件系统
- **LibraryController**: 资源服务器的API控制器，处理库文件头和图片数据请求
- **LibraryParser**: 库文件解析器，负责读取和解析.Lib文件格式

## Requirements

### Requirement 1: 资源服务器连接管理

**User Story:** As a player, I want the game to automatically connect to the resource server, so that I can download game resources on demand.

#### Acceptance Criteria

1. WHEN the game client starts, THE Streaming_Resource_Loader SHALL attempt to connect to the configured Resource_Server
2. WHEN the Resource_Server connection fails, THE Streaming_Resource_Loader SHALL retry connection every 15 seconds
3. WHILE the Resource_Server is unavailable, THE Streaming_Resource_Loader SHALL display a warning message to the player
4. WHEN the Resource_Server becomes available after being unavailable, THE Streaming_Resource_Loader SHALL dismiss the warning message
5. THE Streaming_Resource_Loader SHALL support configurable server address and port through Settings

### Requirement 2: 库文件头下载

**User Story:** As a player, I want the game to download library headers first, so that the game knows what resources are available.

#### Acceptance Criteria

1. WHEN a Library_File is accessed and does not exist locally, THE Streaming_Resource_Loader SHALL download the Library_Header from Resource_Server
2. WHEN the Library_Header is downloaded successfully, THE Streaming_Resource_Loader SHALL create a local file with pre-allocated size
3. WHEN the Library_Header download fails, THE Streaming_Resource_Loader SHALL retry after 1 second
4. THE Library_Header SHALL contain the total file length and index information for all images
5. WHEN writing the Library_Header, THE Streaming_Resource_Loader SHALL write header bytes at the beginning of the pre-allocated file

### Requirement 3: 按需图片资源下载

**User Story:** As a player, I want game resources to download automatically when needed, so that I can play the game without waiting for full download.

#### Acceptance Criteria

1. WHEN an MImage is requested for rendering and its data is empty (all zeros), THE Streaming_Resource_Loader SHALL initiate an async download
2. WHEN downloading an MImage, THE Streaming_Resource_Loader SHALL request the specific image index from Resource_Server
3. WHEN the MImage download completes successfully, THE Streaming_Resource_Loader SHALL write the data to the correct position in the local Library_File
4. WHEN the MImage download completes successfully, THE Streaming_Resource_Loader SHALL create the texture for immediate rendering
5. IF the MImage download fails, THEN THE Streaming_Resource_Loader SHALL retry after 1 second
6. THE Streaming_Resource_Loader SHALL limit concurrent downloads to prevent overwhelming the server (maximum 10 concurrent requests)

### Requirement 4: 占位纹理显示

**User Story:** As a player, I want to see placeholder graphics while resources are downloading, so that the game remains playable during downloads.

#### Acceptance Criteria

1. WHILE an MImage is being downloaded, THE MLibrary SHALL return a Placeholder_Texture for rendering
2. THE Placeholder_Texture SHALL be a simple transparent or solid color texture
3. WHEN the MImage download completes, THE MLibrary SHALL replace the Placeholder_Texture with the actual texture
4. THE Placeholder_Texture SHALL have the same dimensions as the expected image (if known from header)

### Requirement 5: 下载队列管理

**User Story:** As a player, I want resource downloads to be prioritized, so that visible resources load first.

#### Acceptance Criteria

1. THE Download_Queue SHALL use a semaphore to limit concurrent downloads to 10
2. WHEN a download request is made and the semaphore is full, THE Download_Queue SHALL queue the request
3. WHEN a download completes, THE Download_Queue SHALL release the semaphore and process the next queued request
4. THE Download_Queue SHALL process downloads asynchronously without blocking the game loop

### Requirement 6: 本地缓存合并

**User Story:** As a player, I want downloaded resources to be saved locally, so that I don't need to re-download them.

#### Acceptance Criteria

1. WHEN MImage data is downloaded, THE Streaming_Resource_Loader SHALL write it to the correct file position
2. THE Streaming_Resource_Loader SHALL batch multiple image writes to reduce disk I/O
3. THE Streaming_Resource_Loader SHALL process pending writes periodically (once per game loop iteration)
4. WHEN writing to Local_Cache, THE Streaming_Resource_Loader SHALL use file locking to prevent corruption

### Requirement 7: 配置管理

**User Story:** As a server administrator, I want to configure the resource server settings, so that I can deploy the streaming system.

#### Acceptance Criteria

1. THE Settings SHALL include MicroClientEnabled boolean to enable/disable streaming mode
2. THE Settings SHALL include MicroClientIP string for the resource server address
3. THE Settings SHALL include MicroClientPort integer for the resource server port
4. WHEN MicroClientEnabled is false, THE MLibrary SHALL use traditional full-file loading
5. THE Settings SHALL persist configuration to the settings file

### Requirement 8: 错误处理与日志

**User Story:** As a developer, I want comprehensive error logging, so that I can diagnose streaming issues.

#### Acceptance Criteria

1. WHEN a download error occurs, THE Streaming_Resource_Loader SHALL log the error with file name and index
2. WHEN a network exception occurs, THE Streaming_Resource_Loader SHALL increment an exception counter
3. WHEN the exception counter reaches 5, THE Streaming_Resource_Loader SHALL mark the server as unavailable
4. THE Streaming_Resource_Loader SHALL log all download attempts and their results for debugging

### Requirement 9: 向后兼容性

**User Story:** As a player with full game resources, I want the game to work normally, so that existing installations are not affected.

#### Acceptance Criteria

1. WHEN all resources exist locally with valid data, THE MLibrary SHALL load them without contacting Resource_Server
2. WHEN MicroClientEnabled is false, THE MLibrary SHALL behave identically to the original implementation
3. THE streaming system SHALL not modify the Library_File format for compatibility with existing tools

### Requirement 13: 场景感知的按需加载

**User Story:** As a player, I want only the resources needed for the current scene to be loaded, so that the game starts faster and uses less bandwidth.

#### Acceptance Criteria

1. WHEN the game is at the login scene, THE Libraries SHALL only initialize login-related libraries (ChrSel, Prguse, Prguse2, Prguse3, UI_32bit, Title)
2. WHEN the game transitions to the game scene, THE Libraries SHALL initialize additional libraries as needed
3. THE Libraries SHALL NOT initialize all 400+ MapLibs at startup
4. WHEN a specific map is loaded, THE Libraries SHALL only initialize the MapLibs required for that map
5. THE Libraries SHALL use lazy initialization for non-essential libraries

### Requirement 14: 健壮的错误处理

**User Story:** As a player, I want the game to handle download failures gracefully, so that the game doesn't crash when resources are unavailable.

#### Acceptance Criteria

1. WHEN reading a library file that is incomplete or corrupted, THE MLibrary SHALL catch the exception and return gracefully
2. WHEN an MImage cannot be loaded due to incomplete data, THE MLibrary SHALL return a placeholder texture instead of crashing
3. WHEN a header download fails, THE MLibrary SHALL NOT create an empty or corrupted local file
4. THE MLibrary SHALL validate file integrity before attempting to read image data

### Requirement 10: 资源服务器API服务

**User Story:** As a server administrator, I want to deploy a resource server, so that game clients can download resources on demand.

#### Acceptance Criteria

1. THE Resource_Server SHALL provide an HTTP API endpoint GET /api/libheader/{path}/{name} to return library header data
2. THE Resource_Server SHALL provide an HTTP API endpoint GET /api/libimage/{path}/{name}/{index} to return specific image data
3. THE Resource_Server SHALL provide an HTTP API endpoint GET /api/ping to check server availability
4. WHEN a library file is requested, THE Resource_Server SHALL read the file from the configured resource directory
5. WHEN returning library header, THE Resource_Server SHALL return JSON containing TotalLength and Base64-encoded HeaderBytes
6. WHEN returning image data, THE Resource_Server SHALL return the raw binary data for the specified image index
7. IF the requested file does not exist, THEN THE Resource_Server SHALL return HTTP 404 status code
8. IF an error occurs during file reading, THEN THE Resource_Server SHALL return HTTP 500 status code with error message

### Requirement 11: 资源服务器配置管理

**User Story:** As a server administrator, I want to configure the resource server settings, so that I can customize the deployment.

#### Acceptance Criteria

1. THE Resource_Server SHALL support configurable listening port through configuration file
2. THE Resource_Server SHALL support configurable resource directory path through configuration file
3. THE Resource_Server SHALL log all incoming requests for monitoring
4. THE Resource_Server SHALL support CORS to allow cross-origin requests from game clients

### Requirement 12: 资源服务器库文件解析

**User Story:** As a server administrator, I want the server to correctly parse library files, so that clients receive valid resource data.

#### Acceptance Criteria

1. THE Resource_Server SHALL parse Library_File header to extract image count and index information
2. THE Resource_Server SHALL calculate the correct byte offset for each image based on the index
3. THE Resource_Server SHALL read only the requested image data without loading the entire file into memory
4. WHEN parsing library header, THE Resource_Server SHALL extract the first N bytes containing version, image count, and index array
5. THE Resource_Server SHALL correctly map client request paths to actual file system paths regardless of ResourcePath configuration
