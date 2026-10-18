# 分支 dualframework

这条分支是 LayaDualFramework（竖屏小游戏框架，私有仓库）用的 LeanCLR：从 [wqaetly/leanclr](https://github.com/wqaetly/leanclr)
`coreclr` 的 `8804654`（上游 focus-creative-games/leanclr `33e9194` + wqaetly 的 .NET 10 BCL 移植）开出，加上我们自己的改动。
框架以 git submodule 引用这个仓库，锁定这条分支上的某个提交；对 LeanCLR 的改动、它们的说明和来由都只在这里，框架仓库里没有。
这个仓库的 main / unity / coreclr / mono / core 是上游的分支，和这里无关。

框架怎么用它：规则 DLL（C#）在客户端由 LeanCLR 解释执行或经 LeanAOT 编成 C++，和宿主一起编成一个 `STANDALONE_WASM`，
在浏览器、Node、Bun 和微信小游戏里跑；32 位（wasm32），基础类库是 .NET 10 的 linux-arm 运行时包（按规则 DLL 用 ILLink 裁剪）。

## 规则

- 一个改动一个提交，标题以编号开头（`0019 …`），正文写清楚改了什么、为什么、怎么发现的；同一个提交里在下面的表里加一行。
- 只往前加：不 rebase、不强推，框架历史里锁定过的提交都要一直取得到。合上游或 wqaetly 的新提交用 merge。
- 验证在框架里做：把框架的 submodule 指针指过来，跑 `runtime:build`、`runtime:test`、`rules:test`（框架的 runtime/README.md）。
  换了提交但 LeanCLR 的行为不该变时（文档、注释），编出来的 wasm 应该逐字节相同。

## 改动

| 改动 | 提交 | 内容 | 怎么发现的 |
|---|---|---|---|
| 0001 linux-measurement | `ad22ec0` | 在 Linux 上用 Linux 版 CoreLib 跑：errno P/Invoke、平台桩、去掉序号的 P/Invoke 键 | 原生 x64 移植试验 |
| 0002 native spike F1–F20 | `c98d04c` | 原生 x64 移植试验的 F1–F20（+F13c）：.NET 10 BCL 正确性（BclDiff 1945 个用例 → 0 个缺陷）；原生桥（dlopen libSystem.Native）在 `__EMSCRIPTEN__` 下不编 | 原生 x64 移植试验 |
| 0003 wasm32 spike fixes | `5dc9178` | wasm32 移植试验的阻塞项 1–9（32 位 MethodTable 偏移、QCall 指针结构加宽、RawArrayData.Data 偏移、libSystem.Native 垫片、XplatEventLogger 桩、`LEANCLR_THROW_TRACE`），基于 0002 重做；重叠的修正（Unsafe、多维数组边界、空 char* 字符串）保留 0002 的版本 | wasm32 移植试验 |
| 0004 dedupe P/Invoke registrations | `caab06e` | 合并后重复登记的 P/Invoke（`SetErrNo / GetErrNo`、XplatEventLogger）去重；Debug 构建里重复登记会断言 | 合并 0002 + 0003 后 |
| 0005 null Type in custom attributes | `dcd0cc0` | 自定义特性参数里的 null `System.Type`（SerString 0xFF）是 null，不是 BadImageFormat | ET 在 LeanCLR 上的启动探针 |
| 0006 cmake: wasm opt, GC debug off | `6641b78` | `LEANCLR_WASM_OPT` 可配置（宿主传 `-O3 -flto`），`LEANCLR_GC_DEBUG` 默认关 | 性能基准 |
| 0007 field signatures | `197c923` | 常量字段的 `FieldInfo.FieldType` 抛 BadImageFormat：`Signature.Init` 没有成员时总按 PROPERTY 签名（0x08）解析，FIELD 签名（0x06）现在按字段签名读（ET 的 `EnumSingleton<T>` 启动时用到） | wasm 里规则核心启动的调用栈 |
| 0008 GC arena index | `349c49d` | `is_allocated_object` 原来线性扫描全部 arena；GC 在一次调用内不跑，arena 越积越多，`typeof(T)` / `Type.IsValueType` 每次都扫 → 长调用变成平方级。改成按地址排序的 arena 索引（二分）。另：每条 callvirt 上的 `IRuntimeFieldInfo.get_Value` 判断先比参数个数和方法名 | 原生宿主 + gdb 采样 |
| 0009 AOT MVID binding | `c31796a` | AOT 代码绑定到生成它的那组程序集，见下文「绑定」 | 框架的 AOT 流水线 |
| 0010 AOT coverage report | `a87c241` | LeanAOT 记下每个没编的方法和原因（`manifest-report.json`）；剖析构建（`LEANCLR_ENABLE_PROFILE`）导出的每个方法带 `aot` / `why`，并统计解释器 ↔ AOT 的切换次数。只影响剖析构建 | 框架的 AOT 覆盖率工具 |
| 0011 AOT: runtime-resolved natives | `2c454dc` | 不在运行时 API 目录里的 icall 和所有 P/Invoke 不生成 AOT 包装，见下文「原生方法」 | AOT 编 CoreLib 热点方法时 |
| 0012 AOT generic instantiations | `a9ba367` | LeanAOT 编封闭泛型实例化，见下文「泛型」 | 门槛 1：命令耗时 |
| 0013 AOT call-path caches | `35203af` | 编译代码之间的调用开销，见下文「调用开销」 | 门槛 1：桥 / invoker 开销 |
| 0014 AOT exception handling | `5017e38` | LeanAOT 编带 try / catch / finally / fault 的方法（只有带异常过滤器的留给解释器），见下文「异常处理」 | 门槛 1：异常处理还在解释器里 |
| 0015 interpreter: leave before try | `ac4186b` | 解释器：`again: try { try { … goto again; } finally {} } finally {}` 这种目标紧挨在 try 前面的 leave，展开时用 IR 偏移重新判断「目标在不在 try 里」，标签的 nop 不进 IR、目标落在 try 的第一条上，外层 finally 找不到（Debug 断言，Release 解引用空子句）。改成按转换器（IL 偏移）定下的个数取包含 leave 的下一个 finally | 0014 的一致性测试 |
| 0016 invoker name prefilter | `26869e6` | 冷启动：`Shim::get_invoker`（每个类初始化方法表、每个泛型方法膨胀都调）原来为 icall / intrinsic 查表把方法全名拼出来再按字符串查，碰到的每个方法都要拼，命中的只有 CoreLib 的少数几个。每张表加一个 `MethodNameFilter`（`utils/method_name_filter.h`），见下文「冷启动」 | 门槛 2：冷启动 |
| 0017 AOT generic lookup prefilter | `007ae8e` | 冷启动：`find_aot_method_impl` 对每个膨胀的泛型方法都拼完整的实例化键再查。挂 AOT 模块时记下哪些泛型方法定义有编好的实例化，查找先看定义在不在、在才拼键 | 门槛 2：冷启动 |
| 0018 TypeRef resolution cache | `b60b7b4` | 冷启动：TypeRef → TypeDef 每次都按名字到被引用的模块里查（System.Runtime 这类门面还要再经类型转发到 CoreLib）。每个模块按 rid 缓存解析成功的结果，失败不缓存 | 门槛 2：冷启动 |
| 0019 AOT: Span members in place | `af4428b` | `Span<T>` / `ReadOnlySpan<T>` 的下标、`Length`、`IsEmpty`、`Slice` 和 `MemoryMarshal.GetReference` 原地展开，见下文「热循环」 | 交通模拟测速：Span 写法比数组慢 3 倍 |
| 0020 AOT: vector element access | `45991f5` | `ldelem / stelem / ldelema` 直接从 `first_data` 算地址，不再每次经 `klass->by_val->ele_type` 判断有没有多维数组的边界 | 读 0019 之后生成的 C++ |
| 0021 AOT: cached array lengths | `1f77179` | 地址没被取过的数组局部变量和参数，长度存进旁边的 `int32_t`，越界检查和 `ldlen` 读它 | 在生成的 C++ 上逐项去掉检查测份量：越界检查占局部变量写法的 20% |
| 0022 GC.Collect between calls | `dae6627` | 有托管帧在栈上时 `GC.Collect` 只记下请求，下一次调用之间的回收（`maybe_collect` / `collect`）执行 | 0019–0021 的一致性用例：调用中途回收时，解释器丢了调用方求值栈上的临时对象（段错误），只被 Span 指着的数组被回收 |
| 0023 AOT without GC roots | `483dbf3` | LeanAOT `--leanaot-no-gc-roots`：不生成每个方法的 GC 根槽，引用只放在 C++ 局部变量里，见下文「热循环」 | 0021 之后再拆：GC 根的写入占局部变量写法的 21% |
| 0024 ldobj T over a class | `cab383a` | `ldobj` 的结果按求值栈的类型声明，和 `ldind` 一样：引用是 `RtObject*`，不再是类自己的结构体指针（小整数扩成 int32，无符号的按有符号存） | 泛型里读 `in T`（T 是类）：friflo 的 `StackArray<T>.Push` 的数组协变检查取不到 `klass`，`Func<T,…>.Invoke`、`EqualityComparer<T>.Equals` 的调用桥不收类的结构体指针，生成的 C++ 编不过 |
| 0025 boxed this into the interpreter | `00c9f7a` | 编译代码经 callvirt 调一个解释执行的值类型实例方法（接口方法、结构体的虚方法）时，直接调用桥先把箱子跳过对象头再交给虚调用的 invoker，invoker（`fn_interpreter_invoker`）又跳一次：方法读写的是箱子后面一个对象头处的字段。桥现在原样交箱子，和编译过的那一支（`virtual_method_ptr`）一样；带 varargs 的那条路解析出实现后自己跳过对象头 | 游戏的规则核心 AOT 跑第一条命令就抛 NullReferenceException：ET 的 `foreach` 遍历 `IReadOnlyList<SystemObject>`，`List<SystemObject>.Enumerator.MoveNext` 没编、走解释器，读到的 `_list` 是空。解释执行逐条和 .NET 一致 |
| 0026 sizeof(T) with T defined | — | LeanAOT 把 `Unsafe.SizeOf<T>`、`Unsafe.Add<T>` / `Subtract<T>` 原地写成 `sizeof(T)`，T 是结构体时却没把它的定义写进这个 C++ 分片：`Add` 的 `ref T` 参数只给前置声明，`SizeOf` 的签名里根本没有 T，分片里别处没用到 T 时 C++ 编不过。展开时把 T 的完整定义也登记进去 | 规则核心试 friflo（删掉 Fliox 的源码）：`ReadOnlySpan<friflo 的结构体>..ctor(T[], int, int)` 落在一个只有前置声明的分片里，`sizeof` 一个不完整的类型 |

「门槛」是框架阶段 0 的验收：1 = 每条命令的耗时，2 = 冷启动到第一局。每个提交的正文有更细的说明和测量。

## AOT

LeanAOT（`src/leanaot`）把 IL 编成 C++，和运行时一起编进 wasm；LeanAOT 不支持的方法照旧解释执行，两边可以互相调用。

### 绑定（0009）

生成代码写死了它引用的每个程序集的字段偏移、布局和直接调用，所以 LeanAOT 给每个模块写上自己和整个引用闭包的名字 + MVID
（确定性编译和 ILLink 的 MVID 都由内容决定）。加载时逐个比对，对不上时按策略处理：`AotBindingPolicy::Interpret` 让这个模块解释执行
（经 fd_write 打一行 `[leanclr] AOT code of X not used: Y MVID differs …; running it interpreted`），`Strict` 加载即失败
（`AotModule::get_last_rejection()` 给原因）。永远不会有一半新一半旧的代码。`AotModule::get_attached_module_count / get_attached_method_count /
get_rejected_module_count / get_attached_generic_method_count` 给宿主报数。

### 原生方法（0011）

不在运行时 API 目录里的 icall 和所有 P/Invoke 不生成 AOT 包装：包装按名字取函数指针、按 C 签名调，而这里的 icall 返回 `RtResult<T>`
（wasm 里 `Monitor.Enter` 一调就是签名不符的 trap），QCall 大多只登记了 invoker（AOT 包装会抛 EntryPointNotFound）。
LeanAOT 也不编：带异常过滤器（`catch … when`）的方法、vararg、运行时内建的 intrinsic。

### 泛型（0012）

- 集合 = 从 AOT 程序集已编方法体出发的静态闭包（调用、newobj、ldftn、constrained 目标、默认比较器、已实例化类型的虚方法覆盖；
  因异常过滤器没编的方法体也扫）+ 按名字列出的清单（`--leanaot-generic-list`）。静态闭包是下限，清单只补静态看不到的
  （接口 / 虚分派到泛型方法、反射）。剖析构建导出运行时的键，`--leanaot-generic-keys-to-names` 换成「程序集 | 方法全名 | 类型实参」的名字，
  程序集重编后照样能用，对不上的跳过。
- 每个实例化只放在一个 AOT 模块里：在它涉及的全部程序集都在其引用闭包内的模块中选闭包最小的（CoreLib 也算一个 AOT 模块）。
  已编的调用方只在被调方模块的闭包 ⊆ 自己的闭包时直接调，否则经运行时查找，所以 MVID 绑定照样管住直接调用的全部假设。
- 运行时按「程序集 + 定义 rid + 类型实参编码」登记，`find_aot_method_impl` 给膨胀后的方法交出代码并绑定该实例化的 self 槽。
- 和非泛型不同、按解释器语义处理的地方：constrained 静态接口调用（`resolve_constrained_static_method`）、值类型自己实现的
  constrained callvirt（不装箱，`resolve_constrained_virtual_method`）、引用类型的 IL `sizeof`（指针大小）、引用类型的 box。
- 二分排错：`LEANCLR_AOT_GENERIC_RANGE=lo:hi`（只挂登记序号在范围内的）、`LEANCLR_AOT_GENERIC_ONLY=<键>`、
  `LEANCLR_AOT_GENERIC_TRACE=1`（打印每次挂上）；LeanAOT `--leanaot-generic-exclude 子串` 直接不编。

### 调用开销（0013）

无 JIT 的 JavaScriptCore（iOS 小游戏）逐条解释 wasm，编译代码之间每次调用的固定开销比方法体还贵：
callvirt 每个调用点缓存「类 → 实现」（原来每次走接口表 + 协变缓存的哈希查找）；值类型 box 之后只判空的（泛型 `x != null`）不再分配；
Unsafe / MemoryMarshal / SpanHelpers 等运行时实现的辅助函数原地展开（原来经直接调用桥 + intrinsic invoker 拷参数）；
`IsReferenceOrContainsReferences<T>`、`typeof(T)`、`Type.IsValueType` 每个调用点只问运行时一次；`ThrowIfNull(object)` 只在 null 时调。
运行时的泛型接口查找对接口本身跳过协变缓存，反射热路径的 `getenv` 只读一次。
用带函数名的 wasm 在 `node --liftoff --no-wasm-tier-up --cpu-prof` 下剖析得出（`--profiling-funcs` 链接参数）。

### 异常处理（0014、0015）

try / catch / finally / fault 都编，只有异常过滤器留给解释器。全部在方法自己的 C++ 函数里，不用 C++ 异常、不用 setjmp：

- 出错路径（空引用、越界、被调方返回的托管异常……）本来就带着出错指令的 IL 偏移跳到方法末尾的错误标签（`___label_handle_return_error`）；
  有异常处理的方法在那里按子句表顺序找：第一个 try 范围包含该偏移、且是类型匹配的 catch（`Class::is_assignable_from`）或 finally / fault 的子句，
  和解释器的展开循环同一套规则，所以 AOT 和解释执行的 catch 顺序、finally 次数逐条相同。都不匹配就把异常重新设为当前异常
  （期间的 finally 可能自己抛过又接住了别的）、返回 ManagedException，调用方（AOT 或解释器）照常接着展开。
- catch：异常是处理块入口的那个栈值；离开处理块时把它从「当前异常」清掉（解释器的 `pop_throw_flow`）；rethrow 抛最内层所在
  catch 的那个异常。finally / fault 因异常进入时按子句存下在途异常和偏移，endfinally 从下一个子句接着找；在 finally 里
  再抛的异常替换在途的那个（和 .NET 一样）。
- leave：要跑的 finally = try 包含 leave 而不包含目标的子句，按子句顺序；每个 finally 子句一个续延编号变量，endfinally
  按编号 `switch` 到下一个 finally 或目标。throw / rethrow 在这种方法里也走分派，不直接返回；在途的异常对象登记成 GC 根。
- 泛型实例化里的 catch 类型按指令操作数的方式解析；剖析里先报「程序集没编」再报「异常处理」。
- LeanAOT `--leanaot-no-exception-handlers` 关掉（用来二分或对比）。
- 一致性：同一份 IL 在 CoreCLR、AOT（strict）和解释器上三份输出逐行相同（框架的 `runtime:aot:eh`），覆盖 catch 顺序 / 类型不匹配 /
  运行时错误 / 泛型 catch 类型、return 穿过三层 finally、goto 跳出嵌套 try、finally 里抛、rethrow、using / foreach / lock、
  AOT ↔ 解释器双向穿越、处理块里 GC（0022 之后只是请求）。它也找出了解释器的 0015。

### 热循环（0019–0021、0023）

无 JIT 时 wasm 逐条解释，热循环里多一次读内存、多一次调用都直接算进时间。按一个交通模拟的测速（1000 辆车一步，Bun 关 JIT）一项项做：

- 0019：`span[i]`、`Length`、`IsEmpty`、`Slice` 原来都是调用 CoreLib 的方法，每次一个元数据初始化检查、一次 `AotFrameScope`、一个带错误码的返回，Span 写法因此比数组还慢 3 倍。现在按 IL 方法体原地写成比较加地址计算，失败时抛的异常和 IL 一样（下标 IndexOutOfRange，Slice ArgumentOutOfRange，`Slice(start, length)` 用 64 位的判断）；`MemoryMarshal.GetReference(span)` 直接取 `_reference`。实例字段不是恰好 `_reference`、`_length` 的照旧调用。
- 0020：IL 的 `ldelem / stelem / ldelema` 只接受一维、从零开始的数组（ECMA-335 III.4.7），数据从 `first_data` 开始。原来用的是兼顾多维数组的 `vm::Array` 版本，每次读写都要读 `klass->by_val->ele_type`；运行时用 `-fno-strict-aliasing` 编，循环里每写一次内存，这两次读都要重来。`MemoryMarshal.GetArrayDataReference(T[])` 也改用它。
- 0021：地址没被取过（没有 `ldloca / ldarga`）的数组局部变量和参数各带一个 `int32_t` 长度，每次 `stloc / starg` 后更新（null 记 0：越界检查之前已经判过空），参数在入口算一次。越界检查和 `ldlen` 只对「同一个基本块里从这个变量读出来、之后变量没被写过」的值用它（`a[(a = b).Length - 1]` 这种先读数组再改变量的照旧读内存）。长度进了寄存器以后，clang 能合并局部变量上重复的判空：在生成的 C++ 上再去掉全部判空，局部变量写法不再变快。剩下的越界检查全去掉还能再快约 27%，多半是拿读出来的值当下标，提不出循环。
- 0023：`__leanclr_aot_roots` 是每个方法一块的根槽，求值栈上每放一个引用写一次、用完清一次。0022 之后调用当中不回收，这些根没有人扫；`--leanaot-no-gc-roots` 不再生成，`AotFrameScope` 只记方法（栈回溯、异常展开照旧）。默认照旧生成，框架关掉（`RH_AOT_GC_ROOTS=1` 换回来）。

测速（ms，中位数，同一份 DLL 交错跑 5 轮，单次上下浮动约 15%；0023 另外和 0021 交替跑了 10 轮：数组 0.99 → 0.84，局部变量 0.68 → 0.54）：

| | 数组（每次从字段读） | 局部变量 | Span | `fixed` 指针 | friflo ECS |
|---|---:|---:|---:|---:|---:|
| 0018 | 1.46 | 1.15 | 4.31 | 0.47 | 3.88 |
| 0019 | 1.42 | 1.20 | 0.58 | 0.45 | 2.85 |
| 0020 + 0021 | 0.97 | 0.67 | 0.60 | 0.47 | 2.66 |
| 0023 | 0.89 | 0.63 | 0.66 | 0.49 | 2.52 |
| Mono AOT（.NET 10） | 0.84 | 0.54 | 0.64 | 0.47 | 2.02 |

这个测试程序的 AOT wasm 5.42 → 4.84 MB，其中 0.50 MB 是 GC 根。从字段读数组还是比局部变量慢一截：每次写内存后都要重读字段、重新判空，
要合并得知道字段和数组元素不会别名，生成的 C++ 里表达不了。

- 一致性：框架的 `runtime:aot:eh` 加了 `tests/runtime/eh/EhCheck.Arrays.cs`（数组、Span、调用当中的 GC.Collect），带不带 GC 根各跑一遍，
  AOT 和解释器都和 CoreCLR 逐行相同。

## 冷启动（0016–0018）

第一次碰到类型 / 方法时的类型系统工作在无 JIT 的引擎上很贵，这三项去掉的是其中「查名字」的部分：

- 0016：每张 icall / intrinsic 表的 `MethodNameFilter` 从键保守地推出「类的简单名 + 方法名」可能是什么（方法名：每个 `::` 之后、
  到参数表之前在每个 `<` 处截断；类名：类型部分在每个 `<` 处截断后最后一个 `.` `/` `+` 之后的部分），存成排好序的 64 位哈希，
  记住上一个问过的类；类名自己带 `.` `/` `+` 的（C# 不会生成）一律走原来的查表，所以可能命中的方法一定查得到，其余直接跳过拼名。
  icall 的旧例外（字符串比较）只在查到条目时才判断；P/Invoke 登记时只有带 `|` 的键才拼去掉序号的副本（0001），而且按段整段拷贝。
- 0017：按程序集的 rid 表记下有编好实例化的泛型方法定义（不逐条分配，第一次用时排序），并记住上一次的结果；
  二分用的登记序号只在设了 `LEANCLR_AOT_GENERIC_*` 时才建。
- 0018：每个模块的 TypeRef → TypeDef 解析表（TypeDef gid + 1，0 = 还没解析）。

## 已知限制

- GC 不扫原生栈：只在两次托管调用之间回收，一次调用里分配的东西会留到调用结束（宿主在调用之间触发 GC）；调用当中的 `GC.Collect` 只记下请求，下一次回收时执行（0022）。
- `Array.CreateInstance` 带下界的一维数组 `Rank` 报 2，`Array.GetValue / SetValue` 对多维数组抛 ArgumentException（解释器和 AOT 一样，0019–0023 的用例里发现，没修）。
- 编译选项：`LEANCLR_WASM_OPT`（Emscripten 优化参数，0006）、`LEANCLR_GC_DEBUG`（被回收的对象清零，漏登记的根会立刻出错）、
  `LEANCLR_ENABLE_PROFILE`（剖析构建，0010）；运行时环境变量 `LEANCLR_THROW_TRACE=1` 打印抛异常时的解释器调用栈（0003）。
