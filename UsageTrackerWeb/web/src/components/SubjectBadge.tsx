import { useEffect, useRef, useState } from 'react';

interface Props {
  // 后台真实分类；null/undefined 表示空分类（无分类标记）
  subject: string | null | undefined;
}

interface Layer {
  key: number;
  value: string;
  muted: boolean;
}

const EMPTY_LABEL = '空分类';
const ANIMATION_MS = 380;

const toLayer = (value: string | null | undefined, key: number): Layer => ({
  key,
  value: value ?? EMPTY_LABEL,
  muted: !value,
});

/**
 * 分类标签：值变化时旧标签向左滑出并淡出，新标签从右侧滑入并淡入。
 * 采用层队列：每个历史值都有独立的绝对定位离场层，连续多次变化（SSE 高频刷新、
 * 手动分类→关键词重匹配两次跳变）也不会丢失任何一段离场动画。
 */
export default function SubjectBadge({ subject }: Props) {
  const keyRef = useRef(0);
  const prevValueRef = useRef(subject ?? EMPTY_LABEL);
  const [layers, setLayers] = useState<Layer[]>(() => [toLayer(subject, 0)]);
  const timersRef = useRef<number[]>([]);

  useEffect(() => {
    const value = subject ?? EMPTY_LABEL;
    if (value === prevValueRef.current) return;
    prevValueRef.current = value;

    keyRef.current += 1;
    setLayers((current) => [...current, toLayer(subject, keyRef.current)]);

    const timer = window.setTimeout(() => {
      // 动画结束后只保留最新值；若期间又有变化，最新层始终在队列末尾
      setLayers((current) => current.slice(-1));
      timersRef.current = timersRef.current.filter((item) => item !== timer);
    }, ANIMATION_MS);
    timersRef.current.push(timer);
  }, [subject]);

  useEffect(() => () => {
    timersRef.current.forEach((timer) => window.clearTimeout(timer));
  }, []);

  const activeLayer = layers[layers.length - 1];

  return (
    <span
      className={`session-subject-text${activeLayer.muted ? ' muted' : ''}`}
      title={activeLayer.value}
    >
      <span className="subject-badge">
        {layers.slice(0, -1).map((layer) => (
          <span
            key={`leave-${layer.key}`}
            className={`subject-badge-layer is-leaving${layer.muted ? ' muted' : ''}`}
          >
            {layer.value}
          </span>
        ))}
        <span
          key={`enter-${activeLayer.key}`}
          className="subject-badge-layer is-entering"
        >
          {activeLayer.value}
        </span>
      </span>
    </span>
  );
}
