import { Button } from './ui/button';

export function Loading({ children }: { children: string }) {
  return <div className="feedback" role="status"><span className="loader" aria-hidden="true" /><p>{children}</p></div>;
}

export function ErrorNotice({ message, retry }: { message: string; retry?: () => void }) {
  return <div className="error-notice"><p role="alert">{message}</p>{retry ? <Button variant="outline" onClick={retry}>Retry</Button> : null}</div>;
}
