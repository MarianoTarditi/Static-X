import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router-dom';
import { App } from './app';
import { createSession } from './auth/session';
import './styles.css';

const session = createSession();
createRoot(document.getElementById('root')!).render(<StrictMode><BrowserRouter><App session={session} /></BrowserRouter></StrictMode>);
