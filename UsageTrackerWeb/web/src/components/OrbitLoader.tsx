interface Props {
  className?: string;
}

export default function OrbitLoader({ className = '' }: Props) {
  return (
    <div className={`orbit-loader${className ? ` ${className}` : ''}`} role="status" aria-label="正在加载">
      <span />
      <span />
      <span />
    </div>
  );
}
