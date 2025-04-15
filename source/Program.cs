using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MWServer
{
    class Program
    {
        private const int Port = 8080; // 监听8080
        private static ServerConfig _config = new ServerConfig(); // 替换原 RootDir

        // 命令处理
        private static bool _commandMode = false;
        private static readonly object _lock = new object();

        // 命令成员变量
        private static readonly string _userName = Environment.UserName;
        private static readonly string _machineName = Environment.MachineName;
        private static readonly string _prompt = $"<{Environment.UserName}@{Environment.MachineName} > ";

        // 键盘监听线程
        private static Thread _keyboardListenerThread;
        private static bool _isRunning = true;

        // 主要方法
        public static async Task Main(string[] args)
        {

            OUT();

            LocalizationManager.LoadLanguage(_config.Language);     // 加载语言文件

            LoadConfig();               // 加载配置

            await Task.Delay(1000);

            Console.Clear();

            MWServerOut();              // 打印标题

            // 启动键盘监听
            StartKeyboardListener();

            await ServerStart();        // 启动服务器
        }

        /* ====== 标题模块 ====== */
        // 输出标题
        public static void MWServerOut()
        {
            Console.WriteLine("         __      __  ____                                           ");
            Console.WriteLine(" /'\\_/`\\/\\ \\  __/\\ \\/\\  _`\\                                         ");
            Console.WriteLine("/\\      \\ \\ \\/\\ \\ \\ \\ \\,\\_\\_\\     __   _ __   __  __     __   _ __  ");
            Console.WriteLine("\\ \\ \\__\\ \\ \\ \\ \\ \\ \\ \\/_\\__ \\   /'__`\\/\\`'__\\/\\ \\/\\ \\  /'__`\\/\\`'__\\");
            Console.WriteLine(" \\ \\ \\_/\\ \\ \\ \\_/ \\_\\ \\/\\ \\_\\ \\/\\  __/\\ \\ \\/ \\ \\ \\_/ |/\\  __/\\ \\ \\/ ");
            Console.WriteLine("  \\ \\_\\\\ \\_\\ `\\_______/\\ `\\____\\ \\____\\\\ \\_\\  \\ \\___/ \\ \\____\\\\ \\_\\ ");
            Console.WriteLine("   \\/_/ \\/_/'\\/__//__/  \\/_____/\\/____/ \\/_/   \\/__/   \\/____/ \\/_/");

            Console.WriteLine();

            Console.WriteLine(LocalizationManager.GetString("titles.version_info",
                _config.Version,
                _config.TestVersion ?? "",
                _config.OfficialVersion));
            Console.WriteLine(LocalizationManager.GetString("titles.copyright"));
            Console.WriteLine(LocalizationManager.GetString("titles.shortcuts"));
        }

        /* ====== 配置文件模块 ====== */
        private static void LoadConfig()
        {
            // 使用本地化提示
            Console.WriteLine(LocalizationManager.GetString("config.loading",
                Path.GetFullPath(ServerConfig.configPath)));

            try
            {
                if (File.Exists(ServerConfig.configPath))
                {
                    var json = File.ReadAllText(ServerConfig.configPath, Encoding.UTF8);
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling = JsonCommentHandling.Skip
                    };

                    _config = JsonSerializer.Deserialize<ServerConfig>(json, options) ?? new ServerConfig();

                    // 加载配置后重新加载语言文件
                    LocalizationManager.LoadLanguage(_config.Language);

                    // 使用参数化本地化输出
                    Console.WriteLine(LocalizationManager.GetString("config.load_success"));
                    Console.WriteLine(LocalizationManager.GetString("config.resource_path",
                        Path.GetFullPath(_config.Resource))); // 显示完整资源路径
                    Console.WriteLine(LocalizationManager.GetString("config.internal_version",
                        _config.Version));
                    Console.WriteLine(LocalizationManager.GetString("config.official_version",
                        _config.OfficialVersion));

                    // 新增调试信息
                    Console.WriteLine(LocalizationManager.GetString("config.language_file",
                        Path.GetFullPath(_config.Language)));
                }
                else
                {
                    // 使用本地化提示
                    Console.WriteLine(LocalizationManager.GetString("config.not_found"));
                    _config = new ServerConfig();

                    // 创建默认配置文件
                    CreateDefaultConfig();
                }
            }
            catch (JsonException ex)
            {
                Console.WriteLine(LocalizationManager.GetString("config.parse_error", ex.Message));
                Console.WriteLine(LocalizationManager.GetString("config.use_default"));
                _config = new ServerConfig();
            }
            catch (SecurityException ex)
            {
                // 新增安全异常处理
                Console.WriteLine(LocalizationManager.GetString("config.security_error", ex.Message));
                _config = new ServerConfig();
            }
            catch (Exception ex)
            {
                Console.WriteLine(LocalizationManager.GetString("config.generic_error", ex.Message));
                _config = new ServerConfig();
            }
            finally
            {
                // 确保语言文件加载
                if (!LocalizationManager.IsLoaded)
                {
                    LocalizationManager.LoadLanguage(_config.Language);
                }
            }
        }

        // 默认配置创建方法
        private static void CreateDefaultConfig()
        {
            try
            {
                var configDir = Path.GetDirectoryName(ServerConfig.configPath);
                if (!Directory.Exists(configDir))
                {
                    Directory.CreateDirectory(configDir);
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(new ServerConfig(), options);
                File.WriteAllText(ServerConfig.configPath, json, Encoding.UTF8);

                Console.WriteLine(LocalizationManager.GetString("config.default_created",
                    Path.GetFullPath(ServerConfig.configPath)));
            }
            catch (Exception ex)
            {
                Console.WriteLine(LocalizationManager.GetString("config.create_failed", ex.Message));
            }
        }

        /* ====== 服务器输入模块 ====== */
        static async Task ServerStart()
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://+:{Port}/");

            try
            {
                listener.Start();
                Console.WriteLine(LocalizationManager.GetString("server.start", GetLocalIP(), Port));

                while (_isRunning)
                {
                    var context = await listener.GetContextAsync();
                    _ = ProcessRequestAsync(context);
                }
            }
            finally
            {
                _isRunning = false; // 停止线程循环
                listener.Close();
                _keyboardListenerThread?.Join(); // 等待线程结束
            }
        }

        private static async Task ProcessRequestAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                var rawPath = request.Url.AbsolutePath.TrimStart('/');
                var fullPath = GetSafePath(rawPath);

                // 处理目录请求
                if (Directory.Exists(fullPath))
                {
                    // 自动追加默认入口文件
                    fullPath = Path.Combine(fullPath,
                        string.IsNullOrEmpty(_config.EntrancePage) ?
                        "index.html" : _config.EntrancePage);

                    // 二次验证文件存在性
                    if (!File.Exists(fullPath))
                    {
                        Send404(response);
                        return;
                    }
                }

                // 处理文件请求
                if (File.Exists(fullPath))
                {
                    await SendFile(response, fullPath);
                }
                else
                {
                    Send404(response);
                }
            }
            catch (Exception ex)
            {
                SendError(response, 500, LocalizationManager.GetString("errors.500", ex.Message));
            }
            finally
            {
                response.Close();
            }
        }

        /* ====== 文件映射模块 ====== */
        #region Helper Methods
        private static string GetSafePath(string rawPath)
        {
            // 国际化日志输出
            Console.WriteLine(LocalizationManager.GetString("pathmapping.raw_path", rawPath));

            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string resourceRoot = Path.GetFullPath(Path.Combine(exeDir, _config.Resource));

            // 统一路径分隔符处理
            string sanitizedPath = rawPath
                .Replace('\\', Path.DirectorySeparatorChar) // 处理Windows风格路径
                .Replace('/', Path.DirectorySeparatorChar)  // 处理Unix风格路径
                .TrimStart(Path.DirectorySeparatorChar);

            // 防御路径遍历攻击
            if (sanitizedPath.Contains(".." + Path.DirectorySeparatorChar))
            {
                throw new SecurityException(LocalizationManager.GetString("pathmapping.invalid_path"));
            }

            // 构建完整路径
            string fullPath = Path.GetFullPath(Path.Combine(resourceRoot, sanitizedPath));

            // 调试日志国际化
            Console.WriteLine(LocalizationManager.GetString("pathmapping.resource_root", resourceRoot));
            Console.WriteLine(LocalizationManager.GetString("pathmapping.sanitized_path", sanitizedPath));

            // 增强安全验证（考虑大小写敏感系统）
            bool isSafe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ?
                fullPath.StartsWith(resourceRoot, StringComparison.OrdinalIgnoreCase) :
                fullPath.StartsWith(resourceRoot);

            if (!isSafe)
            {
                throw new SecurityException(LocalizationManager.GetString("pathmapping.access_denied"));
            }

            return fullPath;
        }
        #endregion

        private static async Task SendFile(HttpListenerResponse response, string path)
        {
            const int bufferSize = 81920; // 80KB 缓冲区
            var sw = Stopwatch.StartNew();

            try
            {
                // 验证文件属性
                var fileInfo = new FileInfo(path);
                if ((fileInfo.Attributes & FileAttributes.Directory) == FileAttributes.Directory)
                {
                    throw new InvalidOperationException(LocalizationManager.GetString("fileio.is_directory"));
                }

                // 设置缓存头
                response.Headers.Add("Cache-Control", "public,max-age=3600"); // 1小时缓存

                // 流式传输大文件
                using var fileStream = new FileStream(path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    bufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                response.ContentType = GetMimeType(Path.GetExtension(path));
                response.ContentLength64 = fileInfo.Length;

                // 分块传输
                var buffer = new byte[bufferSize];
                int bytesRead;
                while ((bytesRead = await fileStream.ReadAsync(buffer)) > 0)
                {
                    await response.OutputStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                    await response.OutputStream.FlushAsync();
                }

                // 性能日志
                Console.WriteLine(LocalizationManager.GetString("fileio.transfer_complete",
                    path,
                    fileInfo.Length,
                    sw.Elapsed.TotalMilliseconds));
            }
            catch (FileNotFoundException)
            {
                Console.WriteLine(LocalizationManager.GetString("fileio.not_found", path));
                Send404(response);
            }
            catch (IOException ex) when (ex.HResult == -2147024864) // 文件被占用
            {
                Console.WriteLine(LocalizationManager.GetString("fileio.locked", path));
                SendError(response, 423, "File is locked");
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine(LocalizationManager.GetString("fileio.unauthorized", path));
                SendError(response, 403, "Access denied");
            }
            catch (Exception ex)
            {
                Console.WriteLine(LocalizationManager.GetString("fileio.general_error", ex.Message));
                SendError(response, 500, "Internal server error");
            }
        }

        private static void SendError(HttpListenerResponse response, int statusCode, string message)
        {
            response.StatusCode = statusCode;
            var errorDesc = statusCode switch
            {
                404 => LocalizationManager.GetString("errors.404"),
                403 => LocalizationManager.GetString("errors.403"),
                423 => LocalizationManager.GetString("errors.423"),
                _ => LocalizationManager.GetString("errors.generic_error", message)
            };
            var errorPage = GenerateErrorPage(statusCode, errorDesc);
            response.StatusCode = statusCode;
            var buffer = Encoding.UTF8.GetBytes(errorPage);
            response.OutputStream.Write(buffer);
        }

        private static string GenerateErrorPage(int statusCode, string message)
        {
            return $@"<!DOCTYPE html>
                    <html>
                    <head>
                        <title>{statusCode} Error</title>
                        <style>
                            body {{ font-family: sans-serif; text-align: center; padding: 50px; }}
                            .error-box {{ 
                                border: 1px solid #ff4444;
                                background: #ffeeee;
                                padding: 20px;
                                max-width: 600px;
                                margin: 0 auto;
                            }}
                        </style>
                    </head>
                    <body>
                        <div class=""error-box"">
                            <h2>{statusCode} - {message}</h2>
                            <p>{LocalizationManager.GetString("errors.contact_support")}</p>
                        </div>
                    </body>
                    </html>";

        }

        private static void Send404(HttpListenerResponse response)
        {
            response.StatusCode = 404;
            var msg = Encoding.UTF8.GetBytes("404 - Not Found");
            response.OutputStream.Write(msg);
        }

        private static string GetMimeType(string ext) => ext.ToLower() switch
        {
            // 网页基础类型
            ".html" => "text/html; charset=utf-8",
            ".htm" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",

            // 图片类型
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",

            // 字体类型
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",

            // 数据格式
            ".json" => "application/json",
            ".xml" => "application/xml",

            // 文本文件
            ".txt" => "text/plain; charset=utf-8",

            // 默认二进制流（会触发下载）
            _ => "application/octet-stream"
        };

        private static string GetLocalIP()
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    return ip.ToString();
                }
            }
            return "127.0.0.1";
        }

        /* ====== 按键输入模块 ====== */
        // 键盘监听方法
        private static void StartKeyboardListener()
        {
            if (_keyboardListenerThread?.IsAlive == true)
                return;

            _keyboardListenerThread = new Thread(() =>
            {
                while (_isRunning)
                {
                    var key = Console.ReadKey(intercept: true);

                    // 增加命令模式状态检查
                    if (!_commandMode)
                    {
                        // 非命令模式下的快捷键处理
                        if (key.Modifiers == ConsoleModifiers.Shift)
                        {
                            if (key.Key == ConsoleKey.L)
                            {
                                OpenBrowser($"http://{GetLocalIP()}:{Port}");
                            }
                            else if (key.Key == ConsoleKey.N)
                            {
                                EnterCommandMode();
                            }
                        }
                        continue;
                    }

                    // 命令模式专用处理
                    HandleCommandModeInput(key);
                }
            })
            {
                IsBackground = true
            };
            _keyboardListenerThread.Start();
        }

        // 打开浏览器方法
        private static void OpenBrowser(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
                Console.WriteLine(LocalizationManager.GetString("browser.open_success", url));
            }
            catch (Exception ex)
            {
                Console.WriteLine(LocalizationManager.GetString("browser.open_error", ex.Message));
            }
        }


        /* ====== 命令模块 ====== */
        // 新增命令模式处理方法
        private static void EnterCommandMode()
        {
            lock (_lock)
            {
                _commandMode = true;
                Console.Clear();
                DrawCommandInterface();
            }
        }

        // 绘制命令界面
        private static void DrawCommandInterface()
        {
            Console.Clear();
            Console.WriteLine(LocalizationManager.GetString("commands.command_mode_title"));
            Console.Write(_prompt);
            Console.ResetColor();
        }

        /* ====== 命令输入处理 ====== */
        private static void HandleCommandModeInput(ConsoleKeyInfo triggerKey)
        {
            var input = new StringBuilder();
            bool firstPrompt = true;

            while (_commandMode)
            {
                //// 显示提示符逻辑优化
                //if (firstPrompt)
                //{
                //    Console.Write(_prompt);
                //    firstPrompt = false;
                //}

                var key = Console.ReadKey(intercept: true);

                // 退出命令模式检测
                if (key.Modifiers == ConsoleModifiers.Shift && key.Key == ConsoleKey.N)
                {
                    ExitCommandMode();
                    return;
                }

                // 输入处理
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine(); // 确保换行
                    ExecuteCommand(input.ToString());
                    input.Clear();

                    // 仅当保持命令模式时显示新提示符
                    if (_commandMode)
                    {
                        Console.Write(_prompt);
                    }
                }
                else if (key.Key == ConsoleKey.Backspace)
                {
                    if (input.Length > 0)
                    {
                        input.Remove(input.Length - 1, 1);
                        Console.Write("\b \b"); // 精确退格
                    }
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    input.Append(key.KeyChar);
                    Console.Write(key.KeyChar);
                }
            }
        }

        private static void ProcessKey(ConsoleKeyInfo key, StringBuilder input, bool isInitialKey)
        {
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine(); // 换行
                ExecuteCommand(input.ToString());
                input.Clear();
                if (!isInitialKey) Console.Write(_prompt);
            }
            else if (key.Key == ConsoleKey.Backspace)
            {
                if (input.Length > 0)
                {
                    input.Remove(input.Length - 1, 1);
                    Console.Write("\b \b");
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                input.Append(key.KeyChar);
                Console.Write(key.KeyChar);
            }
        }

        // 执行命令
        private static void ExecuteCommand(string rawCommand)
        {
            if (string.IsNullOrWhiteSpace(rawCommand)) return;

            // 保留原大小写但统一处理
            var command = rawCommand.Trim();
            var parts = command.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            switch (parts[0].ToLower()) // 核心命令不区分大小写
            {
                case "mwserver":
                    HandleMWServerCommand(parts.Skip(1).ToArray());
                    break;
                case "exit": // 保留原有的退出命令
                    ExitCommandMode();
                    break;
                default:
                    Console.WriteLine(LocalizationManager.GetString("commands.unknown_command", command));
                    break;
            }
        }

        // 退出命令模式
        private static void ExitCommandMode()
        {
            lock (_lock)
            {
                _commandMode = false;

                // 清除输入缓冲区
                while (Console.KeyAvailable)
                    Console.ReadKey(intercept: true);

                Console.Clear();
                MWServerOut();
                LoadConfig();               // 加载配置
                Console.WriteLine(LocalizationManager.GetString("commands.exit_command_mode"));
                Console.WriteLine(LocalizationManager.GetString("server.start", GetLocalIP(), Port));
                Console.WriteLine(LocalizationManager.GetString("server.server_mode_title"));

            }
        }

        /* ====== 命令实现 ====== */

        // MWserver 命令入口
        private static void HandleMWServerCommand(string[] args)
        {
            if (args.Length > 0 && args[0].ToLower() == "-v")
            {
                DisplayVersionInfo();
            }
            if (args.Length > 0 && args[0].ToLower() == "-l")
            {
                HandleLanguageCommand(args);
            }
            else
            {
                Console.WriteLine(LocalizationManager.GetString("commands.invalid_argument"));
            }
        }

        // MWserver -v 命令
        private static void DisplayVersionInfo()
        {
            Console.WriteLine("Using built-in specs.");
            Console.WriteLine("COLLECT_MWS:");
            Console.WriteLine("\t-MWServer");

            // 版本信息
            Console.WriteLine(LocalizationManager.GetString("commands.version_info.versions.version", _config.Version));
            if (!string.IsNullOrEmpty($"\t{_config.TestVersion}"))
                Console.WriteLine(LocalizationManager.GetString("commands.version_info.versions.test_phase", _config.TestVersion));
            Console.WriteLine(LocalizationManager.GetString("ommands.version_info.versions.official_version", _config.OfficialVersion));

            // 系统适配
            Console.WriteLine(LocalizationManager.GetString("server.os_support_title"));
            foreach (var os in _config.ApplicableTo)
            {
                if (os.Value)
                {
                    var versions = _config.OSVersion.ContainsKey(os.Key)
                        ? _config.OSVersion[os.Key]
                        : "N/A";
                    Console.WriteLine($" - {os.Key.PadRight(7)}: 支持\t({versions})");
                }
            }

            // 编译信息
            Console.WriteLine(LocalizationManager.GetString("commands.version_info.compilation", _config.Compiled));
        }

        // MWserver -l 命令
        // 语言命令处理
        private static void HandleLanguageCommand(string[] args)
        {
            if (args.Length == 1) // MWServer -l
            {
                ListAvailableLanguages();
            }
            else if (args.Length == 2) // MWServer -l xx_xx
            {
                ChangeLanguage(args[1]);
            }
            else
            {
                Console.WriteLine(LocalizationManager.GetString("commands.invalid_argument"));
            }
        }

        // 列出可用语言
        private static void ListAvailableLanguages()
        {
            var langDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "user", "language");
            var currentLang = Path.GetFileNameWithoutExtension(_config.Language);

            try
            {
                if (!Directory.Exists(langDir))
                {
                    Console.WriteLine(LocalizationManager.GetString("language.no_lang_dir"));
                    return;
                }

                var langFiles = Directory.GetFiles(langDir, "*.json");
                if (langFiles.Length == 0)
                {
                    Console.WriteLine(LocalizationManager.GetString("language.no_files"));
                    return;
                }

                Console.WriteLine(LocalizationManager.GetString("language.available"));
                foreach (var file in langFiles)
                {
                    var langCode = Path.GetFileNameWithoutExtension(file);
                    var status = langCode.Equals(currentLang, StringComparison.OrdinalIgnoreCase)
                        ? LocalizationManager.GetString("language.current")
                        : "";
                    Console.WriteLine($"  {langCode,-10} {status}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(LocalizationManager.GetString("language.list_error", ex.Message));
            }
        }

        // 切换语言
        private static void ChangeLanguage(string langCode)
        {
            var newLangPath = Path.Combine("user", "language", $"{langCode}.json");
            var fullPath = Path.GetFullPath(newLangPath);

            try
            {
                if (!File.Exists(fullPath))
                {
                    Console.WriteLine(LocalizationManager.GetString("language.not_found", langCode));
                    return;
                }

                // 更新配置
                _config.Language = newLangPath;
                SaveConfig();

                // 重新加载语言
                LocalizationManager.LoadLanguage(_config.Language);
                Console.WriteLine(LocalizationManager.GetString("language.switched", langCode));

                // 刷新界面
                Console.Clear();
                MWServerOut();
            }
            catch (Exception ex)
            {
                Console.WriteLine(LocalizationManager.GetString("language.change_error", ex.Message));
            }
        }

        // 保存配置文件
        private static void SaveConfig()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(_config, options);
                File.WriteAllText(ServerConfig.configPath, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Console.WriteLine(LocalizationManager.GetString("config.save_error", ex.Message));
            }
        }

        /* ====== 开头打印 ======= */
        public static void OUT()
        {
            var errorCount = 0;
            var requiredResources = new List<(string Type, string Path, Func<bool> Check)>
    {
        ("配置文件", ServerConfig.configPath,
            () => File.Exists(ServerConfig.configPath)),

        ("语言文件", _config.Language,
            () => File.Exists(Path.GetFullPath(_config.Language))),

        ("资源目录", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _config.Resource),
            () => Directory.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _config.Resource))),

        ("入口文件", Path.Combine(_config.Resource, _config.EntrancePage),
            () => File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _config.Resource, _config.EntrancePage)))
    };

            Console.WriteLine("=============== Инициация проверки ресурсов ===============");

            foreach (var resource in requiredResources)
            {
                try
                {
                    var isValid = resource.Check();

                    if (isValid)
                    {
                        Console.WriteLine($"[ OK ] {resource.Path} проверьте правильность");
                    }
                    else
                    {
                        errorCount++;
                        Console.WriteLine($"[ ERROR ] {resource.Path} произошла ошибка");
                        Console.WriteLine($"[ ERROR ] {resource.Path} ошибка RESOURCE_NOT_FOUND");
                    }
                }
                catch (Exception ex)
                {
                    errorCount++;
                    Console.WriteLine($"[ ERROR ] {resource.Path} произошла ошибка");
                    Console.WriteLine($"[ ERROR ] {resource.Path} ошибка {ex.HResult} (0x{ex.HResult:X8})");
                }
            }

            // 附加检查：验证配置文件内容
            try
            {
                if (File.Exists(ServerConfig.configPath))
                {
                    var json = File.ReadAllText(ServerConfig.configPath);
                    JsonSerializer.Deserialize<ServerConfig>(json);
                    Console.WriteLine("[ OK ] Проверен синтаксис конфигурационного файла");
                }
            }
            catch (JsonException ex)
            {
                errorCount++;
                Console.WriteLine($"[ ERROR ] {ServerConfig.configPath} произошла ошибка");
                Console.WriteLine($"[ ERROR ] {ServerConfig.configPath} ошибка JSON_PARSE_ERROR: {ex.Message}");
            }

            Console.WriteLine("===========================================");
            Console.WriteLine(errorCount > 0 ?
                $"{errorCount} найдены критические ошибки, и сервер может работать некорректно! \"\r\n" :
                "Все ключевые ресурсы проверены");
        }
    }

    /* ====== 导入设置啥的 ====== */
    public class ServerConfig
    {
        public static string configPath = "user/config.json";               // 设置路径
        public string EntrancePage { get; set; } = "index.html";    // 默认入口文件

        private string _resource = "url";

        [JsonPropertyName("resource")]
        public string Resource
        {
            get => _resource;
            set => _resource = ValidateResourcePath(value);
        }

        private static string ValidateResourcePath(string path)
        {
            if (path.Contains(".."))
                throw new ArgumentException("路径包含非法字符");

            return path.TrimStart('/');
        }

        // 测试版本号
        [JsonPropertyName("version")]
        public string Version { get; set; } = "unversioned";

        // 正式版本号
        [JsonPropertyName("OfficialVersionNumber")]
        public string OfficialVersion { get; set; } = "0.0.0.0";

        // 测试阶段
        [JsonPropertyName("test")]
        public string? TestVersion { get; set; }  // 可为null


        [JsonPropertyName("language")]
        public string Language { get; set; } = "user/language/zh_cn.json"; // 默认语言文件路径


        // 版本命令实现
        [JsonPropertyName("ApplicableTo")]
        public Dictionary<string, bool> ApplicableTo { get; set; } = new();

        [JsonPropertyName("OSVersion")]
        public Dictionary<string, string> OSVersion { get; set; } = new();

        [JsonPropertyName("Compiled")]
        public string Compiled { get; set; } = "C# NET 8.0";
    }

    /* ====== 语言设置类 ====== */
    public static class LocalizationManager
    {
        private static JsonNode? _languageData;
        private static readonly JsonSerializerOptions _options = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };

        public static bool IsLoaded { get; internal set; }

        public static void LoadLanguage(string filePath)
        {
            try
            {
                var fullPath = Path.GetFullPath(filePath);

                // 检查文件存在性（本地化）
                if (!File.Exists(fullPath))
                {
                    Console.WriteLine(LocalizationManager.GetString("localization.file_not_found", fullPath));
                    return;
                }

                // 读取并解析JSON（本地化错误处理）
                var json = File.ReadAllText(fullPath, Encoding.UTF8);
                _languageData = JsonSerializer.Deserialize<JsonNode>(json, _options);

                // 成功加载提示（带路径参数）
                Console.WriteLine(LocalizationManager.GetString("localization.load_success", fullPath));
            }
            catch (JsonException ex) // JSON解析异常
            {
                Console.WriteLine(LocalizationManager.GetString("localization.json_parse_error",
                    ex.Message,
                    ex.LineNumber ?? 0));
            }
            catch (UnauthorizedAccessException ex) // 权限异常
            {
                Console.WriteLine(LocalizationManager.GetString("localization.access_denied",
                    filePath,
                    ex.Message));
            }
            catch (IOException ex) // 文件IO异常
            {
                Console.WriteLine(LocalizationManager.GetString("localization.io_error",
                    filePath,
                    ex.HResult.ToString("X")));
            }
            catch (Exception ex) // 通用异常
            {
                Console.WriteLine(LocalizationManager.GetString("localization.load_failed",
                    ex.GetType().Name,
                    ex.Message));
            }
        }

        public static string GetString(string key, params object[] args)
        {
            try
            {
                var path = key.Split('.');
                JsonNode? node = _languageData;

                foreach (var segment in path)
                {
                    node = node?[segment];
                    if (node == null) break;
                }

                var value = node?.ToString() ?? key;
                return args.Length > 0 ? string.Format(value, args) : value;
            }
            catch
            {
                return key;
            }
        }
    }
}