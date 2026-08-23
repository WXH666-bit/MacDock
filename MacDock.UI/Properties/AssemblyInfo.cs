using System.Runtime.CompilerServices;

// 让单元测试可以直接验证视图模型的内部纯函数（如菜单栏时间格式化）
[assembly: InternalsVisibleTo("MacDock.Tests")]
