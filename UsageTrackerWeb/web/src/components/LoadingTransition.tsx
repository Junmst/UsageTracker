import { useEffect, useState, type ReactNode } from 'react';
import OrbitLoader from './OrbitLoader';

interface Props {
  loading: boolean;
  children: ReactNode;
  className?: string;
}

export default function LoadingTransition({ loading, children, className = '' }: Props) {
  const [exiting, setExiting] = useState(false);

  useEffect(() => {
    if (loading) {
      setExiting(false);
      return undefined;
    }

    setExiting(true);
    const timer = window.setTimeout(() => setExiting(false), 1100);
    return () => window.clearTimeout(timer);
  }, [loading]);

  return (
    <div className={`loading-transition${className ? ` ${className}` : ''}${loading ? ' is-loading' : ''}${exiting ? ' is-exiting' : ''}`}>
      <div className="loading-transition-content">{children}</div>
      {(loading || exiting) && (
        <div className="loading-transition-overlay" aria-hidden={!loading}>
          <OrbitLoader />
        </div>
      )}
    </div>
  );
}
