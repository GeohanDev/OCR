import { createContext, useContext, useState, useEffect } from 'react';
import type { ReactNode } from 'react';
import { apiClient } from '../api/client';
import type { User } from '../types';

interface AuthContextValue {
  user: User | null;
  isLoading: boolean;
  isAuthenticated: boolean;
  login: (token: string) => Promise<void>;
  logout: () => void;
  isManagerOrAbove: boolean;
  isAdmin: boolean;
}

const AuthContext = createContext<AuthContextValue | null>(null);

// Key used to prevent auto-re-login after an explicit sign-out
const LOGGED_OUT_KEY = 'auth-logged-out';

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<User | null>(null);
  const [isLoading, setIsLoading] = useState(true);

  useEffect(() => {
    // If the user explicitly signed out this session, show the login page
    if (sessionStorage.getItem(LOGGED_OUT_KEY)) {
      setIsLoading(false);
      return;
    }

    const token = sessionStorage.getItem('access_token');
    if (token) {
      fetchCurrentUser().catch(() => autoLogin());
    } else {
      autoLogin();
    }
  }, []);

  // Silently obtain a demo JWT so every API call is authenticated.
  // Falls back gracefully if demo mode is disabled or the API is down.
  const autoLogin = async () => {
    try {
      const res = await apiClient.post('/auth/demo-login');
      sessionStorage.setItem('access_token', res.data.accessToken);
      const { data } = await apiClient.get('/auth/me');
      setUser(data);
    } catch {
      // Demo mode disabled or API unavailable — user will see the login page
    } finally {
      setIsLoading(false);
    }
  };

  const fetchCurrentUser = async () => {
    try {
      const { data } = await apiClient.get('/auth/me');
      setUser(data);
    } catch (err) {
      sessionStorage.removeItem('access_token');
      throw err;
    } finally {
      setIsLoading(false);
    }
  };

  const login = async (token: string) => {
    sessionStorage.removeItem(LOGGED_OUT_KEY);
    sessionStorage.setItem('access_token', token);
    setIsLoading(true);
    await fetchCurrentUser();
  };

  const logout = () => {
    sessionStorage.removeItem('access_token');
    sessionStorage.setItem(LOGGED_OUT_KEY, '1');
    setUser(null);
    window.location.href = '/login';
  };

  return (
    <AuthContext.Provider value={{
      user,
      isLoading,
      isAuthenticated: !!user,
      login,
      logout,
      isManagerOrAbove: user?.role === 'Manager' || user?.role === 'Admin',
      isAdmin: user?.role === 'Admin',
    }}>
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth() {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used inside AuthProvider');
  return ctx;
}
