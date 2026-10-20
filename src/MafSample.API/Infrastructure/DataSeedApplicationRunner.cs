using Microsoft.EntityFrameworkCore;

namespace MafSample.API.Infrastructure
{
    /// <summary>
    /// 数据种子初始化运行器，应用启动时自动执行 galoS@2026-04-24 00:00:00
    /// </summary>
    [Service]
    public class DataSeedApplicationRunner : IApplicationRunner
    {
        private readonly ApplicationDbContext _context;

        public DataSeedApplicationRunner(
            ApplicationDbContext context
            )
        {
            _context = context;
        }

        /// <summary>
        /// 初始化数据库并写入种子数据（幂等），请根据业务需求实现
        /// </summary>
        public async Task RunAsync(ApplicationArguments args, CancellationToken cancellationToken = default)
        {
            await _context.Database.EnsureCreatedAsync(cancellationToken);
            // TODO: 根据业务需求实现种子数据初始化逻辑
            // 示例流程：
            //   1. await _context.Database.EnsureCreatedAsync(cancellationToken);
            //   2. 幂等检查（检查种子数据是否已存在）
            //   3. 创建实体（使用 fluent builder 方法设置属性）
            //   4. 反射设置 protected 属性（TenantId、CreatorName 等）
            //   5. await _context.SaveChangesAsync(cancellationToken);
            //throw new NotImplementedException("请实现种子数据初始化逻辑，或删除此类");
        }
    }
}
