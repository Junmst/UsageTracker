import { useEffect, useRef } from 'react';
import { invalidateResponseCache } from './api';

// 后台数据变更事件总线：全局只开一条 SSE 连接，浏览器原生自动重连。
// settings —— 配置/分类变化（手动分类、关键词重匹配、规则、热键、空闲时长、区间）
// data     —— 记录集变化（新记录、记录结束、删除）
export type DataEventType = 'settings' | 'data';

type Listener = (type: DataEventType) => void;

const listeners = new Set<Listener>();
let source: EventSource | null = null;

export function connectDataEvents(): void {
  if (source) return;
  const es = new EventSource('/api/events');
  source = es;

  const dispatch = (type: DataEventType) => {
    // 看板窗口最小化/不可见时不刷新；恢复可见时统一补刷一次（见 visibilitychange）
    if (document.visibilityState === 'hidden') return;
    // 先清空 GET 缓存，监听者随后的 refetch 才能拿到新数据
    invalidateResponseCache();
    listeners.forEach((listener) => {
      try {
        listener(type);
      } catch {
        // 单个页面回调异常不影响其他页面
      }
    });
  };

  es.addEventListener('settings', () => dispatch('settings'));
  es.addEventListener('data', () => dispatch('data'));
  // 出错时浏览器会自动重连，无需手工处理；重连成功后基线之后的新变化会继续推送。

  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') dispatch('data');
  });
}

/**
 * 订阅后台数据变更，变更时局部重新拉取本页数据（不整页刷新）。
 * @param callback 收到变更时执行（通常是本页的 force load）
 * @param enabled 是否启用（可传入页面是否可见，隐藏的缓存页面不产生请求）
 * @param types 只接收指定类型；默认 settings 与 data 都接收
 */
export function useDataChange(
  callback: () => void,
  enabled = true,
  types: DataEventType[] = ['settings', 'data']
): void {
  const callbackRef = useRef(callback);
  callbackRef.current = callback;
  const typesKey = types.join(',');

  useEffect(() => {
    if (!enabled) return;
    const accepted = new Set(typesKey.split(','));
    const listener: Listener = (type) => {
      if (accepted.has(type)) callbackRef.current();
    };
    listeners.add(listener);
    return () => {
      listeners.delete(listener);
    };
  }, [enabled, typesKey]);
}
