## 变更说明

<!-- 一句话说明这个 PR 做了什么、为什么。 -->

## 关联

- 阶段 / 任务：
- 关联 issue：

## 影响面

- [ ] 运行时行为变更（如有，说明兼容性）
- [ ] 公开契约变更（程序集名 / CLI 参数 / 配置键 / 环境变量 / 数据目录 / 数据库 schema）
- [ ] 仅文档
- [ ] 仅构建、CI 或工具

## 自检

- [ ] `dotnet build -c Release` 0 warning / 0 error
- [ ] `dotnet test -c Release --no-build` 通过（附通过 / 跳过数）
- [ ] `bash tools/audit-conventions.sh` 全绿
- [ ] 涉及打包脚本时，`bash build/package/legal-files.sh /tmp/legal-check` 产出四个法务文件
- [ ] 新增文档已挂到 `docs/README.md` 索引，链接可解析
- [ ] 未提交 >5MB 二进制，未使用 `git add -f`

## 回滚

<!-- 出问题时怎么退回上一個可用状态。 -->
